namespace Affiant.Core.Filters;

using System.Diagnostics;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Completion-stage filter that routes WriteProposal results through ReviewGate — the framework's
/// non-blocking filing default (P5a). Fires after each auto-invoked tool. If the tool result
/// deserializes as a WriteProposal, the proposal is routed through
/// <see cref="ReviewGate.FileForReviewAsync"/> using the pipeline's per-invocation scope
/// (<see cref="ToolInvocationContext.Services"/>). Silently skips when IReviewContextProvider or
/// ReviewGate are not registered in the DI container, so the filter is safe to register globally
/// even in hosts that do not use the full review infrastructure. Runs identically on both adapters
/// — it is a neutral <see cref="ICompletionStageFilter"/>, invoked inside
/// <c>AffiantAutoFunctionInvocationBridge</c>'s own <c>pipeline.RunAsync</c> call on SK and inside
/// <c>AffiantFunctionInvocationMiddleware</c>'s single onion on MAF; neither adapter needs its own
/// copy of this logic.
///
/// <para>
/// <b>Non-blocking filing, ordering-proof (P5a, area-4 d1-host-bypass finding B):</b> the blocking
/// predecessor of this filter called <c>ReviewGate.FileReviewAsync</c>, which awaits the reviewer's
/// decision inline — structurally deadlocks over single-connection SignalR
/// (host-apps#25, <c>MaximumParallelInvocationsPerClient = 1</c>; see
/// <see cref="ReviewGate.FileReviewAsync"/>'s own XML docs). This filter now calls the non-blocking
/// <see cref="ReviewGate.FileForReviewAsync"/> instead: on <see cref="ReviewFilingResult.RequiresReview"/>
/// (a human reviewer must act — the Evidence Card broadcast already happened inside
/// <see cref="ReviewGate.FileForReviewAsync"/> itself), this filter sets
/// <see cref="ToolInvocationContext.Terminate"/> so the model's turn ends the moment the card is
/// filed, matching the pattern both reference hosts independently hand-built as bespoke,
/// host-specific filing filters (see the area-4 d1-host-bypass evidence pack) before this
/// non-blocking behavior became the framework's own default — this filter's shape mirrors that
/// proven pattern, kept domain-agnostic since it lives in the framework. On
/// <see cref="ReviewFilingResult.Decided"/> (already resolved without a client round trip — a
/// StandingOrder auto-approval, a Referral escalation, or an idempotent replay) this filter does
/// NOT terminate: nothing further needs to happen this turn, so the model keeps the tool's own
/// original result and continues normally.
/// </para>
///
/// <para>
/// <b>Why registration order no longer matters (affiant#25, item 1 of this wave).</b> Unlike a
/// host's own <c>IAutoFunctionInvocationFilter</c> (SK) — which competes for position inside SK's
/// own filter list and previously had to be force-inserted at index 0 to survive the bridge's
/// unconditional <c>Terminate</c> overwrite — this filter is not an SK-native filter at all. It
/// runs INSIDE <c>AffiantAutoFunctionInvocationBridge</c>'s own neutral <c>pipeline.RunAsync</c>
/// call, so its <c>Terminate</c> decision is baked into <c>resultContext.Terminate</c> before the
/// bridge's own final assignment ever runs — no SK filter-list position competition exists for it
/// to lose. Combined with affiant#25's fix (the bridge now preserves a genuinely downstream SK
/// filter's own <c>Terminate</c> too, via OR rather than overwrite), a host registering ITS OWN
/// additional completion-stage logic no longer needs HR Portal's
/// <c>kernel.AutoFunctionInvocationFilters.Insert(0, ...)</c> workaround either way. Proven, not
/// assumed, by <c>ReviewGateFilterOrderingTests</c> — the real bridge, registered the NORMAL
/// (appended) way via <c>AddAffiantSemanticKernel</c>'s standard DI chain, ends a turn on
/// <see cref="ReviewFilingResult.RequiresReview"/> with zero special registration handling.
/// </para>
///
/// <para>
/// <b>Filing-failure handling (P1a, affiant#22 / FV-9 — carried over intact from the blocking
/// predecessor, only the filing call changed):</b> a non-cancellation exception from
/// <see cref="ReviewGate.FileForReviewAsync"/> means the proposal was never durably filed (any
/// exception surviving that call is, by construction, a pre-persist failure — <see cref="ReviewGate"/>
/// itself retries and swallows post-persist Evidence Card broadcast failures so filing still reports
/// success there; see its class remarks). This filter converts that failure into a typed
/// <see cref="ToolError"/> (<see cref="ToolErrorFilter.ReviewFilingFailedCode"/>) so the model is told
/// the truth — the action was not queued for review, not silently lost — emits an
/// <c>affiant.review.filing_failed</c> OTel event, and best-effort broadcasts a
/// <see cref="TransportEvent.SystemNotification"/> on the same session-group channel used for
/// Evidence Cards. <see cref="OperationCanceledException"/> still propagates unchanged — cancellation
/// is not a filing failure. Deliberately does NOT set <see cref="ToolInvocationContext.Terminate"/>
/// on this path (unlike the success path above): nothing was filed, so the model should see this
/// like any other typed tool failure and is free to retry or tell the user, rather than being forced
/// to end the turn over a docket-store failure that may already be resolved.
/// </para>
/// </summary>
public sealed class ReviewGateFilter(ILogger<ReviewGateFilter> logger) : ICompletionStageFilter
{
    /// <summary>
    /// The single, model-facing turn-ending message used whenever a write proposal requires a human
    /// reviewer's decision. Only reachable via <see cref="ReviewFilingResult.RequiresReview"/> — see
    /// this filter's class remarks for why <see cref="ReviewFilingResult.Decided"/> deliberately
    /// leaves the tool's own result untouched instead of using a message of its own.
    /// </summary>
    private const string TurnEndingMessage =
        "This action has been filed for review — check the Evidence Card to approve, reject, or amend it.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        await next(context);

        var resultString = context.Result as string ?? context.Result?.ToString();
        if (string.IsNullOrEmpty(resultString))
            return;

        WriteProposal? proposal;
        try
        {
            var envelope = JsonSerializer.Deserialize<ToolEnvelope>(resultString, JsonOptions);
            proposal = envelope as WriteProposal;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // STJ throws JsonException for malformed JSON and NotSupportedException when a
            // polymorphic type (ToolEnvelope) lacks the required $type discriminator.
            // Both mean the result is not a WriteProposal — skip silently.
            return;
        }

        if (proposal is null)
            return;

        var contextProvider = context.Services.GetService<IReviewContextProvider>();
        if (contextProvider is null)
        {
            logger.LogDebug(
                "ReviewGateFilter: IReviewContextProvider not registered; skipping review for {ToolName}",
                proposal.ToolName);
            return;
        }

        var reviewContext = contextProvider.BuildReviewContext(proposal);
        if (reviewContext is null)
        {
            logger.LogDebug(
                "ReviewGateFilter: no ambient review context available; skipping review for {ToolName}",
                proposal.ToolName);
            return;
        }

        var gate = context.Services.GetService<ReviewGate>();
        if (gate is null)
        {
            logger.LogDebug(
                "ReviewGateFilter: ReviewGate not registered; skipping review for {ToolName}",
                proposal.ToolName);
            return;
        }

        ReviewFilingResult filing;
        try
        {
            // CancellationToken.None, deliberately: FileForReviewAsync persists the Pending
            // DocketEntry then broadcasts its Evidence Card using the same token — once the tool has
            // actually run and produced a WriteProposal, filing it must complete as a unit. Using the
            // ambient cancellationToken here (ultimately tied to the client connection) would let a
            // disconnect land between persist and broadcast, leaving a Pending entry no client ever
            // saw. The entry remains recoverable via a host's own approvals surface or the docket's
            // TTL expiry either way — a disconnect specifically must not cause that gap.
            filing = await gate.FileForReviewAsync(proposal, reviewContext, CancellationToken.None);
            logger.LogInformation(
                "ReviewGateFilter: filed review for {ToolName}: {FilingType}",
                proposal.ToolName, filing.GetType().Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var toolError = new ToolError(
                ToolName: proposal.ToolName,
                Timestamp: DateTimeOffset.UtcNow,
                Code: ToolErrorFilter.ReviewFilingFailedCode,
                Message: $"The write proposal for '{proposal.ToolName}' was NOT filed and was NOT " +
                         "queued for review. No reviewer will see this request. Please retry, or " +
                         "contact a reviewer directly if the problem persists.",
                Retryable: false);

            // Seal the model-facing evidence FIRST — everything below is best-effort observability
            // that must never mask this ToolError, per the "no silent branch" principle.
            context.Result = toolError.ToJsonString();

            logger.LogError(ex,
                "ReviewGateFilter: ReviewGate.FileForReviewAsync failed for {ToolName} — proposal was " +
                "NOT filed and NOT queued for review",
                proposal.ToolName);

            RecordFilingFailureEvent(toolError, ex);

            await NotifyClientBestEffortAsync(
                context.Services, reviewContext.SessionId, proposal.ToolName, cancellationToken);

            return;
        }

        // Filed successfully. RequiresReview: a human must act — the Evidence Card is already on
        // its way to the client (broadcast inside FileForReviewAsync itself), so end the turn here
        // rather than let the model keep reasoning while a decision is pending. Decided: the review
        // already resolved without a client round trip (StandingOrder auto-approval, a Referral
        // escalation, or an idempotent replay) — nothing further to do this turn, so leave the
        // tool's own result and Terminate untouched and let the model continue normally.
        if (filing is ReviewFilingResult.RequiresReview)
        {
            context.Terminate = true;
            context.Result = TurnEndingMessage;
        }
    }

    private static void RecordFilingFailureEvent(ToolError toolError, Exception ex)
    {
        var target = AffiantTelemetry.FindAffiantActivity() ?? Activity.Current;
        target?.AddEvent(new ActivityEvent("affiant.review.filing_failed",
            tags: new ActivityTagsCollection
            {
                { "tool_error.code", toolError.Code },
                { "exception.type", ex.GetType().Name }
            }));
    }

    /// <summary>
    /// Best-effort <see cref="TransportEvent.SystemNotification"/> broadcast on the same
    /// session-group channel <see cref="ReviewGate"/> uses for Evidence Cards (both hosts render
    /// SystemNotification since Area-2 P1). Resolved from the per-invocation scope, matching how this
    /// filter resolves <see cref="ReviewGate"/>/<see cref="IReviewContextProvider"/> — silently skips
    /// when no <see cref="IStreamingTransport"/> is registered. Any failure here — including the
    /// transport not being registered, or the broadcast itself throwing — is swallowed: it must never
    /// mask the <see cref="ToolError"/> already sealed onto <see cref="ToolInvocationContext.Result"/>.
    /// </summary>
    private async Task NotifyClientBestEffortAsync(
        IServiceProvider services, string sessionId, string toolName, CancellationToken cancellationToken)
    {
        var transport = services.GetService<IStreamingTransport>();
        if (transport is null) return;

        try
        {
            await transport.BroadcastToGroupAsync(
                sessionId,
                TransportEvent.SystemNotification,
                new
                {
                    level = "error",
                    message = $"Your request to {toolName} could not be filed for review and was " +
                              "not queued. Please try again."
                },
                cancellationToken);
        }
        catch (Exception notifyEx)
        {
            logger.LogWarning(notifyEx,
                "ReviewGateFilter: best-effort SystemNotification broadcast failed after a filing " +
                "failure for {ToolName}",
                toolName);
        }
    }
}
