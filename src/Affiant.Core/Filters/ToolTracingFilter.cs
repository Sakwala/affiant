using System.Diagnostics;
using Affiant.Core.Observability;
using Microsoft.SemanticKernel;

namespace Affiant.Core.Filters;

/// <summary>
/// Framework filter that creates a single <c>execute_tool</c> span for every SK function
/// invocation. Registered by <c>AddAffiantCore()</c> — hosts do not need to register it manually.
///
/// The span carries <c>gen_ai.tool.name</c> (the SK function name) and <c>tool_status</c>
/// (<c>"ok"</c>, <c>"empty"</c>, or <c>"error"</c>). It remains active throughout the full inner
/// filter chain (host <c>ContextExtractor</c> subclasses, <c>TaskInferenceFilter</c>, and the plugin),
/// so all inner events and attributes are automatically attached to this span.
///
/// Placed inside <c>ToolErrorFilter</c> in the SK pipeline. When a plugin throws, this filter
/// records <c>ActivityStatusCode.Error</c> and <c>tool_status="error"</c> on the span before
/// disposal. The <c>affiant.tool_error</c> event (emitted by the outer <c>ToolErrorFilter</c>)
/// lands on the nearest parent <c>Affiant.Framework</c> span via <c>FindAffiantActivity</c>
/// walk-up, because the <c>execute_tool</c> span is disposed before <c>ToolErrorFilter</c>'s
/// catch block runs.
/// </summary>
public sealed class ToolTracingFilter : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        var activity = AffiantTelemetry.AffiantActivitySource.StartActivity(
            "execute_tool", ActivityKind.Internal);
        activity?.SetTag("gen_ai.tool.name", context.Function.Name);

        try
        {
            await next(context);

            if (activity is not null)
            {
                var resultText = context.Result.GetValue<string>();
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
