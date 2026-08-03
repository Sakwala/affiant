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
/// Completion-stage filter that routes WriteProposal results through ReviewGate.
///
/// Fires after each auto-invoked tool. If the tool result deserializes as a WriteProposal, the
/// proposal is routed through ReviewGate.FileReviewAsync using the pipeline's per-invocation
/// scope (<see cref="ToolInvocationContext.Services"/>). Silently skips when IReviewContextProvider
/// or ReviewGate are not registered in the DI container, so the filter is safe to register globally
/// even in hosts that do not use the full review infrastructure.
///
/// <para>
/// <b>Filing-failure handling (P1a, affiant#22 / FV-9):</b> a non-cancellation exception from
/// <see cref="ReviewGate.FileReviewAsync"/> means the proposal was never durably filed (any
/// exception surviving that call is, by construction, a pre-persist failure — <see cref="ReviewGate"/>
/// itself retries and swallows post-persist Evidence Card broadcast failures so filing still reports
/// success there; see its class remarks). This filter converts that failure into a typed
/// <see cref="ToolError"/> (<see cref="ToolErrorFilter.ReviewFilingFailedCode"/>) so the model is told
/// the truth — the action was not queued for review, not silently lost — emits an
/// <c>affiant.review.filing_failed</c> OTel event, and best-effort broadcasts a
/// <see cref="TransportEvent.SystemNotification"/> on the same session-group channel used for
/// Evidence Cards. <see cref="OperationCanceledException"/> still propagates unchanged — cancellation
/// is not a filing failure.
/// </para>
/// </summary>
public sealed class ReviewGateFilter(ILogger<ReviewGateFilter> logger) : ICompletionStageFilter
{
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

        try
        {
            var outcome = await gate.FileReviewAsync(proposal, reviewContext);
            logger.LogInformation(
                "ReviewGateFilter: filed review for {ToolName}: {OutcomeType}",
                proposal.ToolName, outcome.GetType().Name);
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
                "ReviewGateFilter: ReviewGate.FileReviewAsync failed for {ToolName} — proposal was " +
                "NOT filed and NOT queued for review",
                proposal.ToolName);

            RecordFilingFailureEvent(toolError, ex);

            await NotifyClientBestEffortAsync(
                context.Services, reviewContext.SessionId, proposal.ToolName, cancellationToken);
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
