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
/// invocation-stage/MAF failure — never raw into SK's auto-invocation loop.
/// </para>
///
/// <para>
/// <b>Retry safety at the completion seam (area-3 P2 fix round — corrects a disproven claim).</b>
/// An earlier version of these remarks claimed a completion-stage exception with
/// <c>ToolExecuted == false</c> was "structurally impossible" to retry into a double execution.
/// That claim was FALSE: two independent adversarial refuters reproduced <c>next(context)</c> being
/// called twice at this seam. The completion-stage terminal's <c>next(context)</c>
/// (<c>Affiant.SemanticKernel.Filters.AffiantAutoFunctionInvocationBridge</c>) is SK's OWN
/// auto-invocation continuation, not the tool — it nested-invokes the real tool through a SEPARATE
/// <see cref="ToolInvocationContext"/> at the invocation-stage seam. A host-registered SK filter
/// outside Affiant's bridges (or SK's own argument binding) can throw before that nested invocation
/// ever happens, leaving <c>ToolExecuted == false</c> on the completion-stage context — the retry
/// branch below would then call SK's continuation a SECOND time, genuinely re-executing the tool.
/// The real fix is <see cref="ToolInvocationContext.NextIsToolBody"/> (default
/// <see langword="true"/>): the completion-stage bridge sets it <see langword="false"/> because its
/// <c>next()</c> is not the tool body, and the retry branch below is gated on both
/// <c>!ToolExecuted &amp;&amp; NextIsToolBody</c>. MAF's single onion and
/// <c>ManualToolInvoker</c>'s completion terminal both leave it at the default
/// <see langword="true"/> — <c>next()</c> genuinely IS (or trivially leads to) the tool there, so
/// retrying is correct and unchanged. See that property's remarks for the full finding.
/// </para>
/// </summary>
public class ToolErrorFilter(
    ILogger<ToolErrorFilter> logger,
    TimeProvider? timeProvider = null) : IToolInvocationFilter
{
    /// <summary>
    /// The clock the <see cref="ToolError.Timestamp"/> of every error this filter builds is stamped
    /// from. Defaults to <see cref="TimeProvider.System"/>; <c>AddAffiantCore</c> registers exactly
    /// that as the DI default.
    /// </summary>
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

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

            if (toolError.Retryable && !context.NextIsToolBody)
            {
                // Area-3 P2 fix round: retryable classification, but next() at this seam is NOT the
                // tool body (SK completion stage) — retrying would re-invoke SK's own continuation a
                // second time, genuinely re-executing the tool. Surface the ToolError without
                // retrying; do not call next() again.
                logger.LogWarning(
                    "Tool {FunctionName} failed with retryable {ErrorCode} but this seam's next() " +
                    "is not the tool body (NextIsToolBody=false) — surfacing without retry to avoid " +
                    "double-executing the tool",
                    context.FunctionName, toolError.Code);
            }

            if (toolError.Retryable && context.NextIsToolBody)
            {
                // Retry exactly once — re-run the entire downstream filter chain. Safe: ToolExecuted
                // is still false here (the exception above was caught by the second catch clause,
                // which only matches when it is), AND NextIsToolBody confirms next() re-runs only
                // the tool body at this seam (area-3 P2 fix round) — so the tool has not produced a
                // result yet and retrying cannot re-execute anything other than the tool itself.
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

    private ToolError MapExceptionToToolError(string toolName, Exception ex)
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
            Timestamp: _time.GetUtcNow(),
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
