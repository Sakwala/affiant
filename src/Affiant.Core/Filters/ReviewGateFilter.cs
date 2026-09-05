namespace Affiant.Core.Filters;

using System.Diagnostics;
using System.Text.Json;
using Affiant.Abstractions.Exceptions;
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
/// (<see cref="ToolInvocationContext.Services"/>). <b>Fails closed</b> (protocol rules CV-1, CV-2):
/// a tool the framework's registry declares write-capable is refused — never passed through — when
/// the review path cannot be reached, or when it returned something other than a proposal.
/// Runs identically on both adapters
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
/// <b>Fail-closed, not skip (CV-1, CV-2 — closes affiant#75).</b> Until <c>1.0.0-beta.1</c> this
/// filter returned quietly in three branches, each at debug-log level: no
/// <see cref="IReviewContextProvider"/> registered, no ambient review context available, and no
/// <see cref="ReviewGate"/> registered. In every one of them the tool's own result — the raw
/// proposal — stayed on <see cref="ToolInvocationContext.Result"/>, so the model was free to report
/// an unfiled, unreviewed write as done, and the only signal was a log line nobody watches. That is
/// the failure mode for exactly the call sites that most need the gate: a queue consumer, a cron
/// trigger, an alarm, a background job — any seam outside the interactive path the framework's own
/// reference wiring assumes. All three are now refusals: the tool's result becomes the error arm
/// carrying <see cref="ToolErrorCodes.WireUpInvalid"/> and nothing is passed through. Two of the
/// three (no provider, no gate) are also caught before any turn runs, by
/// <c>AffiantWireUpValidator</c> in this package and <c>AffiantStartupValidator</c> in
/// <c>Affiant.SemanticKernel</c> — this is the backstop for the third, which only a live request can
/// know, and for a container that cannot be enumerated at startup.
/// </para>
///
/// <para>
/// <b>A declared write tool that returns a non-proposal is refused too.</b> A result that does not
/// deserialize as a <see cref="WriteProposal"/> is skipped when the framework's tool registry does
/// not declare the tool write-capable — that is an ordinary read tool passing through. When the
/// registry <em>does</em> declare it write-capable, the same result is a refusal: a write tool's
/// declared result is a proposal (GT-6), and one that returned a bare success string either wrote
/// something itself or lost its proposal, and neither may be reported to the model as a completed,
/// reviewed write.
/// </para>
///
/// <para>
/// <b>The honest boundary (GT-6).</b> This filter runs <em>after</em> the tool body, because that is
/// the only seam either host framework exposes. A tool that opens its own connection and writes
/// inside its body is therefore <b>outside the guarantee</b> — no filter and no wire-up check can
/// see it, and the framework says so rather than implying a coverage it does not have. What the
/// framework does guarantee is that such a tool cannot commit <em>through</em> it: the gate never
/// calls a write tool's own execute, no public API lets a tool commit through the framework, and a
/// tool declared write-capable that does not hand back a proposal is refused rather than skipped.
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
public sealed class ReviewGateFilter(
    ILogger<ReviewGateFilter> logger,
    TimeProvider? timeProvider = null) : ICompletionStageFilter
{
    /// <summary>
    /// The clock the <see cref="ToolError.Timestamp"/> of a filing failure is stamped from.
    /// Defaults to <see cref="TimeProvider.System"/>; <c>AddAffiantCore</c> registers exactly that
    /// as the DI default.
    /// </summary>
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

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
            // polymorphic type (ToolEnvelope) lacks the required `kind` discriminator (AF-5).
            // Both mean the result is not a WriteProposal — which is a refusal for a declared
            // write tool and a pass-through for anything else.
            proposal = null;
        }

        if (proposal is null)
        {
            // A read tool's result passes through untouched. A tool the registry declares
            // write-capable does not: a write tool's declared result is a proposal (GT-6).
            if (DeclaredWriteTool(context) is { } declaredName)
                RefuseWireUp(context, declaredName, NonProposalReason(declaredName));
            return;
        }

        // GT-4: the arguments the model passed are part of the material an entry id derives from, and
        // this seam is where they are known — a tool serializes its proposal without them, so a
        // filing that left them out would give two calls that differ only in what the model passed
        // the same row identity. They are carried for identity alone; what is SWORN about a field is
        // what an interceptor or the inference port says (PV-1).
        if (context.Arguments is { Count: > 0 } arguments)
        {
            proposal = proposal with
            {
                Arguments = arguments.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            };
        }

        var contextProvider = context.Services.GetService<IReviewContextProvider>();
        if (contextProvider is null)
        {
            RefuseWireUp(context, proposal.ToolName,
                $"No {nameof(IReviewContextProvider)} is registered, so the review this write must " +
                "pass through cannot be routed to anyone. The write was NOT filed and NOT queued " +
                "for review. Fix: register a host IReviewContextProvider that builds a ReviewContext " +
                "from the caller's identity, or call the gate directly with an explicit turn context " +
                "from this call site.");
            return;
        }

        var reviewContext = contextProvider.BuildReviewContext(proposal);
        if (reviewContext is null)
        {
            RefuseWireUp(context, proposal.ToolName,
                $"The registered {nameof(IReviewContextProvider)} could not build a review context " +
                "for this call — the ambient identity a review is routed by is not available at this " +
                "seam. The write was NOT filed and NOT queued for review. A new call site (a queue " +
                "consumer, a cron trigger, an alarm) must call the gate directly with an explicit " +
                "turn context rather than reuse an ambient-context filter.");
            return;
        }

        var gate = context.Services.GetService<ReviewGate>();
        if (gate is null)
        {
            RefuseWireUp(context, proposal.ToolName,
                $"No {nameof(ReviewGate)} is registered, so there is nothing to file this write " +
                "with. The write was NOT filed and NOT queued for review. Fix: call " +
                "services.AddAffiantCore(...) in this application's composition root.");
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
        catch (AffiantRefusalException refusal)
        {
            // The gate refused rather than failed: the proposal swore to nothing (GT-3), or a policy
            // broke its own contract (CV-1). Nothing was filed and nothing was broadcast, so this is
            // not the filing-failure path — the model is told the refusal's own code, which is the
            // error arm of the three-kind tool result, and the turn is NOT terminated: there is no
            // card for anyone to look at.
            context.Result = new ToolError(
                ToolName: proposal.ToolName,
                Timestamp: _time.GetUtcNow(),
                Code: refusal.Code,
                Message: refusal.Message,
                Retryable: false).ToJsonString();

            logger.LogWarning(refusal,
                "ReviewGateFilter: the gate refused {ToolName} with {Code} — the proposal was NOT " +
                "filed and NOT queued for review",
                proposal.ToolName, refusal.Code);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var toolError = new ToolError(
                ToolName: proposal.ToolName,
                Timestamp: _time.GetUtcNow(),
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

    /// <summary>
    /// The registry name of the tool at this seam when the framework declares it write-capable, or
    /// <see langword="null"/> when the registry does not know it or declares it a read.
    ///
    /// <para>
    /// Asked of <see cref="IAffiantToolRegistry"/> rather than of the result's shape, deliberately:
    /// the whole point of the check is to catch a declared write tool whose result shape is
    /// <em>wrong</em>. A container with no registry — a host running the neutral pipeline without
    /// <c>AddAffiantCore</c> — answers "not declared", and the startup validators are what refuse
    /// that wiring; this branch does not invent a refusal from an absent registry.
    /// </para>
    /// </summary>
    private static string? DeclaredWriteTool(ToolInvocationContext context)
    {
        var registry = context.Services.GetService<IAffiantToolRegistry>();
        var descriptor = registry?.Find(context.FunctionName, context.PluginName)
                         ?? registry?.Find(context.FunctionName);
        if (descriptor is null) return null;
        if (descriptor.Operation.Kind == Operation.ReadQuery.Kind) return null;

        return descriptor.PluginName is null
            ? descriptor.FunctionName
            : $"{descriptor.PluginName}.{descriptor.FunctionName}";
    }

    private static string NonProposalReason(string toolName) =>
        $"'{toolName}' is declared write-capable but returned a result that is not a write " +
        "proposal, so there was nothing for the gate to file and no evidence for a reviewer to see. " +
        "A gated write tool's declared result is a proposal (GT-6). Fix: return a WriteProposal from " +
        "the tool, or declare it a read tool with services.AddAffiantReadTool(...). A tool that " +
        "opens its own connection and writes inside its body is outside this framework's guarantee " +
        "and must not be declared write-capable.";

    /// <summary>
    /// Seals a <c>wireup-invalid</c> refusal onto the tool result and does not terminate the turn.
    /// Nothing was filed, so the model should see this like any other typed tool failure rather than
    /// be told a card is waiting.
    /// </summary>
    private void RefuseWireUp(ToolInvocationContext context, string toolName, string reason)
    {
        context.Result = new ToolError(
            ToolName: toolName,
            Timestamp: _time.GetUtcNow(),
            Code: ToolErrorCodes.WireUpInvalid,
            Message: reason,
            Retryable: false).ToJsonString();

        logger.LogError(
            "ReviewGateFilter refused {ToolName}: {Reason}", toolName, reason);
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
                new SystemNotificationPayload(
                    "error",
                    $"Your request to {toolName} could not be filed for review and was " +
                    "not queued. Please try again."),
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
