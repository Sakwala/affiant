namespace QuickstartHost.DevSeam;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using QuickstartHost.Agent;

/// <summary>
/// A development-only way to put an Evidence Card in front of a reviewer without a model key.
///
/// <para>
/// Everything the review lifecycle does — approve, reject, amend, expire, resubmit — happens
/// <em>after</em> an affidavit is filed. Filing normally needs a live model turn, which makes
/// every one of those behaviours slow, non-deterministic and impossible to test without model
/// access. This seam files the affidavit directly instead, and skips nothing else: the affidavit
/// comes from the same <see cref="LeaveProposalBuilder"/> and the same projection a real tool call
/// uses, and it is filed through the real <c>ReviewGate</c>, which evaluates the approval policy
/// and broadcasts the card exactly as it would for a model-proposed write. Every decision made
/// afterwards travels the ordinary path through <see cref="Hubs.ChatHub"/>.
/// </para>
///
/// <para>
/// <b>The gate.</b> Both routes are refused unless the host is running in Development
/// <em>and</em> <c>DevSeam:Enabled</c> is true. The routes are mapped unconditionally and the gate
/// is an endpoint filter rather than a conditional <c>MapPost</c>, so a request that arrives with
/// the gate shut gets a genuine 404 instead of being absorbed by the static-file fallback and
/// answered with an HTML page. Both conditions are re-read per request, so there is exactly one
/// place a future change would have to get wrong to open this in production.
/// </para>
/// </summary>
public static class DevSeamEndpoints
{
    /// <summary>The configuration key that must be true, in Development, for the seam to answer.</summary>
    public const string EnabledKey = "DevSeam:Enabled";

    private static readonly IReadOnlyDictionary<string, string> CannedProposal =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Deliberately blank: a blank mandatory field is what makes the reviewer's employee
            // picker and the mandatory-field gate observable. Supply it through `overrides`, or
            // pick one on the card.
            ["Employee"] = "",
            ["StartDate"] = "2026-11-02",
            ["EndDate"] = "2026-11-06",
            ["LeaveType"] = "Annual",
            ["Days"] = "5",
            ["Reason"] = "Family visit overseas.",
        };

    public static void MapDevSeamEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/dev").AddEndpointFilter<DevSeamGateFilter>();
        group.MapPost("/propose", ProposeAsync);
        group.MapGet("/docket/{id:guid}", GetDocketAsync);
    }

    /// <summary>
    /// Files one leave-request proposal and returns as soon as its card is on the wire.
    ///
    /// Pass <c>entityId</c> to make it update-shaped: the projection then loads that leave request
    /// and the filed affidavit carries its id plus, per field, the value the database holds today.
    /// </summary>
    private static async Task<IResult> ProposeAsync(
        DevProposeRequest? request,
        LeaveProposalBuilder proposals,
        ReviewGate reviewGate,
        IStreamingTransport streamingTransport,
        IDocketStore docketStore,
        IApprovalPolicyEvaluator evaluator,
        AffiantCoreOptions coreOptions,
        ILoggerFactory loggerFactory,
        ILogger<DevSeamMarker> logger,
        CancellationToken cancellationToken)
    {
        var sessionId = string.IsNullOrWhiteSpace(request?.SessionId)
            ? $"dev-seam-{Guid.NewGuid():N}"
            : request.SessionId;

        var stated = new Dictionary<string, string>(CannedProposal, StringComparer.Ordinal);
        if (request?.Overrides is { Count: > 0 } overrides)
        {
            foreach (var (name, value) in overrides)
                stated[name] = value;
        }

        // A blank value is not a stated value. Dropping it here is what lets the projection tag the
        // field ProvenanceSource.Empty — Rule 7 — instead of swearing a user said "".
        var nonBlank = stated
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var affidavit = request?.EntityId is { } entityId
            ? proposals.BuildUpdate(entityId, nonBlank)
            : proposals.BuildCreate(nonBlank);

        if (request?.EntityId is { } missing && affidavit.EntityId is null)
        {
            return Results.BadRequest(new { error = $"No leave request exists with id {missing}." });
        }

        var toolName = affidavit.EntityId is null
            ? RequestLeavePlugin.FunctionName
            : AmendLeavePlugin.FunctionName;

        var gate = request?.TtlSeconds is { } ttlSeconds && ttlSeconds > 0
            // The docket TTL is a host-wide option, so a per-request deadline needs a gate carrying
            // its own options. This is the same ReviewGate type on the same stores and transport —
            // the filing path under test is not stubbed, only its clock is shortened, which is what
            // makes the expiry behaviour testable in under a minute instead of half an hour.
            ? new ReviewGate(
                streamingTransport,
                docketStore,
                evaluator,
                new AffiantCoreOptions
                {
                    DefaultDocketTtl = TimeSpan.FromSeconds(ttlSeconds),
                    DocketExpiryWarningWindow = coreOptions.DocketExpiryWarningWindow,
                    EnableObservability = coreOptions.EnableObservability,
                },
                loggerFactory.CreateLogger<ReviewGate>())
            : reviewGate;

        var proposal = new WriteProposal(toolName, DateTimeOffset.UtcNow, affidavit);
        var reviewContext = new ReviewContext(
            SessionId: sessionId,
            TenantId: "default",
            UserId: Review.HttpReviewContextProvider.DemoUserId,
            ReviewerUserId: Review.HttpReviewContextProvider.DemoUserId,
            Affidavit: affidavit);

        var filing = await gate.FileForReviewAsync(proposal, reviewContext, cancellationToken);
        var docketId = filing switch
        {
            ReviewFilingResult.RequiresReview requires => requires.EntryId,
            ReviewFilingResult.Decided decided => decided.Outcome.DocketId,
            _ => Guid.Empty,
        };

        logger.LogInformation(
            "[DevSeam] Filed {ToolName} as DocketEntry {DocketId} in session {SessionId}",
            toolName, docketId, sessionId);

        return Results.Ok(new DevProposeResponse(sessionId, docketId));
    }

    /// <summary>
    /// Reads one entry's server-side state straight from the docket store, independent of whatever
    /// a browser is currently showing. That independence is the point: it is how a test can assert
    /// "the store already says this" before the client has caught up.
    /// </summary>
    private static async Task<IResult> GetDocketAsync(
        Guid id, IDocketStore docketStore, CancellationToken cancellationToken)
    {
        var entry = await docketStore.GetDocketEntryAsync(id, cancellationToken);
        return entry is null
            ? Results.NotFound()
            : Results.Ok(new DevDocketResponse(
                entry.Status.ToString(), entry.ExpiresAt, entry.Amendments));
    }
}

/// <summary>Category-only marker for <c>ILogger&lt;T&gt;</c>; this type has no members.</summary>
public sealed class DevSeamMarker;

/// <summary>
/// The gate itself, applied to the whole <c>/api/dev</c> group: Development environment
/// <em>and</em> <c>DevSeam:Enabled</c>. Anything else is a plain 404, indistinguishable from an
/// entry that does not exist — a probe cannot tell "the seam is shut" from "the seam is absent".
/// </summary>
public sealed class DevSeamGateFilter(IHostEnvironment environment, IConfiguration configuration)
    : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>(DevSeamEndpoints.EnabledKey))
            return ValueTask.FromResult<object?>(Results.NotFound());

        return next(context);
    }
}

/// <param name="SessionId">
/// The session (and SignalR group) to file the review under. Omit it and a fresh
/// <c>dev-seam-&lt;guid&gt;</c> id is used — but then nothing is listening to that group, so pass
/// the id the page is already joined to if you want to see the card.
/// </param>
/// <param name="Overrides">
/// Affidavit field name to replacement value, e.g. <c>{"Employee": "Amara Silva"}</c>. A blank
/// value clears the field, which leaves it tagged with no provenance.
/// </param>
/// <param name="TtlSeconds">
/// How long the filed entry stays pending. Defaults to the host's own docket TTL. Set it low to
/// watch the expiry lifecycle without waiting out the real one.
/// </param>
/// <param name="EntityId">
/// The id of an existing leave request. Supplying it makes the proposal update-shaped: the card
/// carries the entity's id and each field's current database value.
/// </param>
public sealed record DevProposeRequest(
    string? SessionId,
    Dictionary<string, string>? Overrides,
    int? TtlSeconds = null,
    int? EntityId = null);

/// <summary>What the seam hands back: the session the card went to, and the entry's id.</summary>
public sealed record DevProposeResponse(string SessionId, Guid DocketId);

/// <summary>One entry's server-side state. <c>status</c> is the framework's own review status.</summary>
public sealed record DevDocketResponse(
    string Status,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, object?>? Amendments);
