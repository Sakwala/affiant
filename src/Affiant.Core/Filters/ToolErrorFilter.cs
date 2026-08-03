using System.Diagnostics;
using System.Net;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Microsoft.Extensions.Logging;

namespace Affiant.Core.Filters;

/// <summary>
/// Catches exceptions thrown by tools (or downstream filters), converts them
/// into structured <see cref="ToolError"/> envelopes, and retries retryable
/// failures exactly once. Records <c>affiant.tool_error</c> span events on the
/// active <c>execute_tool</c> span for observability.
///
/// Must be registered outermost among the neutral filters (framework spec §3.12.4 step 1, area-3
/// P2 ruling 2) so its <c>next(context)</c> wraps the entire downstream chain — including
/// <c>DeterministicShortCircuit</c>, so a bug in a host's <c>IIntentInterceptor</c> also becomes a
/// typed <see cref="ToolError"/> instead of propagating raw out of the neutral pipeline.
///
/// <para>
/// <b>Tool-body vs. post-processing (area-3 P2 ruling 3):</b> this filter's catch decision is
/// governed by <see cref="ToolInvocationContext.ToolExecuted"/>, which the bridge/middleware's
/// terminal delegate sets the instant the real tool call succeeds — <em>before</em> any post-tool
/// filter's own logic runs. An exception caught while <c>ToolExecuted</c> is still
/// <see langword="false"/> is a genuine tool-body failure (or a pre-tool
/// <c>DeterministicShortCircuit</c> failure — the tool never ran on this attempt, so retrying is
/// safe): mapped to a <see cref="ToolError"/>, retried once if classified retryable, exactly as
/// before. An exception caught while <c>ToolExecuted</c> is already <see langword="true"/> is a
/// post-processing failure (a completion-stage filter, e.g. <c>TaskInferenceMergeFilter</c> or
/// <c>ReviewGateFilter</c>, threw after the tool already produced a result) — per gate ruling 3
/// (extractor policy = surface-and-continue), <see cref="ToolInvocationContext.Result"/> is left
/// exactly as the tool produced it: never discarded, never retried into a second tool execution,
/// never reported to the model as a tool failure. Only logged + an <c>affiant.extractor.failed</c>
/// OTel event, via the same helper <c>ContextExtractor</c>/<c>TaskInferenceMergeFilter</c> use for
/// their own self-guarding (this is the generic backstop for anything that reaches this filter
/// without having self-guarded).
/// </para>
///
/// <para>
/// <b>SK completion-stage participation (area-3 P2 ruling 1):</b>
/// <c>Affiant.SemanticKernel.Filters.BridgeStages.CompletionStage</c> includes this filter alongside
/// the two neutral completion-stage filters, so an exception from ANY completion-stage filter
/// reaches the model as a typed <see cref="ToolError"/> with the same observable shape as an
/// invocation-stage/MAF failure — never raw into SK's auto-invocation loop — without double-firing
/// retries or telemetry for the tool's own failure: by the time SK's completion-stage bridge's
/// terminal delegate (<c>next(context)</c>, which triggers the real invocation, itself already
/// wrapped by this same filter at the invocation-stage seam) returns, either the tool's own failure
/// was already resolved into a <see cref="ToolError"/> string there (no exception reaches this
/// instance at the completion-stage seam at all), or the tool genuinely succeeded
/// (<c>ToolExecuted</c> is <see langword="true"/> by the time a completion-stage filter could
/// throw) — so this filter's retry branch is only ever reached here for a pre-tool-style failure
/// that occurs before the nested invocation-stage call, which does not exist at this seam;
/// completion-stage failures are therefore always handled by the surface-and-continue branch above,
/// never retried.
/// </para>
/// </summary>
public class ToolErrorFilter(ILogger<ToolErrorFilter> logger) : IToolInvocationFilter
{
    /// <summary>
    /// Code for a <see cref="ToolError"/> produced when <c>ReviewGateFilter</c>'s call to
    /// <c>ReviewGate.FileReviewAsync</c> throws (P1a, affiant#22 / FV-9) — the WriteProposal was
    /// not filed and was not queued for review. Kept as a forwarding alias to
    /// <see cref="ToolErrorCodes.ReviewFilingFailed"/> (P2, area-3 ruling 4) for source
    /// compatibility with existing references to this constant.
    /// </summary>
    public const string ReviewFilingFailedCode = ToolErrorCodes.ReviewFilingFailed;

    /// <summary>
    /// Sentinel <c>extractor.type</c> tag value used by this filter's own generic post-processing
    /// backstop (see class remarks) when the specific filter type that threw is not known —
    /// distinguishes the backstop path from a self-guarding filter's own precisely-tagged
    /// <c>affiant.extractor.failed</c> event (e.g. <c>ContextExtractor</c>'s <c>GetType().Name</c>).
    /// </summary>
    public const string UnattributedPostToolFilterType = "(unattributed post-tool filter — caught by ToolErrorFilter backstop)";

    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException)
        {
            throw; // Cancellation must propagate — never convert to ToolError
        }
        catch (Exception ex) when (context.ToolExecuted)
        {
            // Ruling 3: the tool already produced a genuine result before this exception occurred —
            // this is a post-processing failure, not a tool failure. Surface-and-continue: never
            // touch context.Result, never retry (retrying would re-execute the already-succeeded
            // tool), never report failure to the model.
            RecordPostToolFilterFailureEvent(context.FunctionName, ex);
            logger.LogError(ex,
                "Post-tool filter failed for {FunctionName} after the tool already produced a " +
                "result — result preserved, NOT surfaced to the model (surface-and-continue)",
                context.FunctionName);
        }
        catch (Exception ex)
        {
            var toolError = MapExceptionToToolError(context.FunctionName, ex);

            RecordToolErrorEvent(toolError, ex);
            logger.LogWarning(ex,
                "Tool {FunctionName} failed with {ErrorCode} (retryable: {Retryable})",
                context.FunctionName, toolError.Code, toolError.Retryable);

            if (toolError.Retryable)
            {
                // Retry exactly once — re-run the entire downstream filter chain. Safe: ToolExecuted
                // is still false here (the exception above was caught by the second catch clause,
                // which only matches when it is), so the tool has not produced a result yet.
                try
                {
                    await next(context);
                    return; // Retry succeeded — context.Result is already set by the tool
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception retryEx) when (context.ToolExecuted)
                {
                    // The retry's tool call succeeded but a post-processing filter then threw.
                    // Same ruling-3 surface-and-continue treatment — never discard the retry's
                    // genuine result.
                    RecordPostToolFilterFailureEvent(context.FunctionName, retryEx);
                    logger.LogError(retryEx,
                        "Post-tool filter failed for {FunctionName} after the retried tool call " +
                        "already produced a result — result preserved, NOT surfaced to the model " +
                        "(surface-and-continue)",
                        context.FunctionName);
                    return;
                }
                catch (Exception retryEx)
                {
                    var retryError = MapExceptionToToolError(context.FunctionName, retryEx) with
                    {
                        Retryable = false // Second failure is always non-retryable
                    };

                    RecordToolErrorEvent(retryError, retryEx);
                    logger.LogWarning(retryEx,
                        "Tool {FunctionName} retry failed with {ErrorCode} — surfacing to LLM",
                        context.FunctionName, retryError.Code);

                    context.Result = retryError.ToJsonString();
                    return;
                }
            }

            context.Result = toolError.ToJsonString();
        }
    }

    private static ToolError MapExceptionToToolError(string toolName, Exception ex)
    {
        // DbUpdateException is checked by type name to avoid a compile-time dependency on
        // Microsoft.EntityFrameworkCore in the framework core package. EF Core hosts still
        // get the correct retryable classification at runtime.
        var (code, retryable) = ex switch
        {
            _ when ex.GetType().Name == "DbUpdateException" => (ToolErrorCodes.DbTimeout, true),
            TimeoutException => (ToolErrorCodes.DbTimeout, true),
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.ServiceUnavailable
                => (ToolErrorCodes.UpstreamUnavailable, true),
            ArgumentException => (ToolErrorCodes.ValidationFailed, false),
            InvalidOperationException => (ToolErrorCodes.ValidationFailed, false),
            _ => (ToolErrorCodes.Unknown, false)
        };

        return new ToolError(
            ToolName: toolName,
            Timestamp: DateTimeOffset.UtcNow,
            Code: code,
            Message: ex.Message, // Never ex.ToString() — no stack traces in user-facing text
            Retryable: retryable);
    }

    private static void RecordPostToolFilterFailureEvent(string toolName, Exception ex) =>
        AffiantTelemetry.RecordExtractorFailedEvent(UnattributedPostToolFilterType, toolName, ex);

    private static void RecordToolErrorEvent(ToolError toolError, Exception ex)
    {
        // When backend OTel diagnostics are enabled, Activity.Current may be a backend child span.
        // Walk up to the nearest Affiant.Framework span so the event lands on our trace.
        var target = AffiantTelemetry.FindAffiantActivity() ?? Activity.Current;
        target?.AddEvent(new ActivityEvent("affiant.tool_error",
            tags: new ActivityTagsCollection
            {
                { "tool_error.code", toolError.Code },
                { "tool_error.retryable", toolError.Retryable },
                { "exception.type", ex.GetType().Name }
            }));
    }
}
