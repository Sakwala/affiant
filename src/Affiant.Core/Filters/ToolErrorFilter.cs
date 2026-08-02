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
/// Must be registered before downstream pipeline filters so its <c>next(context)</c>
/// wraps the entire downstream chain.
/// </summary>
public class ToolErrorFilter(ILogger<ToolErrorFilter> logger) : IToolInvocationFilter
{
    /// <summary>
    /// Code for a <see cref="ToolError"/> produced when <c>ReviewGateFilter</c>'s call to
    /// <c>ReviewGate.FileReviewAsync</c> throws (P1a, affiant#22 / FV-9) — the WriteProposal was
    /// not filed and was not queued for review. Placed here alongside this filter's own
    /// exception-mapped codes (<c>DB_TIMEOUT</c>, <c>UPSTREAM_UNAVAILABLE</c>, <c>VALIDATION_FAILED</c>,
    /// <c>UNKNOWN</c> in <see cref="MapExceptionToToolError"/> below) since a full <c>ToolErrorCodes</c>
    /// registry is out of scope for this change (P2, framework spec backlog).
    /// </summary>
    public const string ReviewFilingFailedCode = "REVIEW_FILING_FAILED";

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
        catch (Exception ex)
        {
            var toolError = MapExceptionToToolError(context.FunctionName, ex);

            RecordToolErrorEvent(toolError, ex);
            logger.LogWarning(ex,
                "Tool {FunctionName} failed with {ErrorCode} (retryable: {Retryable})",
                context.FunctionName, toolError.Code, toolError.Retryable);

            if (toolError.Retryable)
            {
                // Retry exactly once — re-run the entire downstream filter chain
                try
                {
                    await next(context);
                    return; // Retry succeeded — context.Result is already set by the tool
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
            _ when ex.GetType().Name == "DbUpdateException" => ("DB_TIMEOUT", true),
            TimeoutException => ("DB_TIMEOUT", true),
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.ServiceUnavailable
                => ("UPSTREAM_UNAVAILABLE", true),
            ArgumentException => ("VALIDATION_FAILED", false),
            InvalidOperationException => ("VALIDATION_FAILED", false),
            _ => ("UNKNOWN", false)
        };

        return new ToolError(
            ToolName: toolName,
            Timestamp: DateTimeOffset.UtcNow,
            Code: code,
            Message: ex.Message, // Never ex.ToString() — no stack traces in user-facing text
            Retryable: retryable);
    }

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
