using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;

namespace Affiant.Core.Filters;

/// <summary>
/// Framework filter that creates a single <c>execute_tool</c> span for every tool invocation.
/// Registered by <c>AddAffiantCore()</c> — hosts do not need to register it manually.
///
/// The span carries <c>gen_ai.tool.name</c> (the function name) and <c>tool_status</c>
/// (<c>"ok"</c>, <c>"empty"</c>, or <c>"error"</c>). It remains active throughout the full inner
/// filter chain (host <c>ContextExtractor</c> subclasses, <c>TaskInferenceMergeFilter</c>, and the
/// tool), so all inner events and attributes are automatically attached to this span.
///
/// Placed inside <c>ToolErrorFilter</c> in the pipeline. When a tool throws, this filter
/// records <c>ActivityStatusCode.Error</c> and <c>tool_status="error"</c> on the span before
/// disposal. The <c>affiant.tool_error</c> event (emitted by the outer <c>ToolErrorFilter</c>)
/// lands on the nearest parent <c>Affiant.Framework</c> span via <c>FindAffiantActivity</c>
/// walk-up, because the <c>execute_tool</c> span is disposed before <c>ToolErrorFilter</c>'s
/// catch block runs.
/// </summary>
public sealed class ToolTracingFilter : IToolInvocationFilter
{
    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        var activity = AffiantTelemetry.AffiantActivitySource.StartActivity(
            "execute_tool", ActivityKind.Internal);
        activity?.SetTag("gen_ai.tool.name", context.FunctionName);

        try
        {
            await next(context);

            if (activity is not null)
            {
                var resultText = context.Result as string ?? context.Result?.ToString();
                activity.SetTag("tool_status", string.IsNullOrEmpty(resultText) ? "empty" : "ok");
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("tool_status", "error");
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }
}
