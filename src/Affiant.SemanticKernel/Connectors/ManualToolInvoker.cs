using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Affiant.SemanticKernel.Connectors;

// Uses kernel.InvokeAsync to fire the full IFunctionInvocationFilter chain —
// identical filter-captured state to SK's automatic invocation path.
public class ManualToolInvoker(ILogger<ManualToolInvoker> logger) : IManualToolInvoker
{
    public async Task<FunctionResultContent> CaptureAndInvokeAsync(
        FunctionCallContent functionCall, Kernel kernel, CancellationToken ct)
    {
        var pluginName = functionCall.PluginName ?? string.Empty;
        var functionName = functionCall.FunctionName;

        logger.LogInformation(
            "[ManualToolInvoker] Invoking {Plugin}.{Function} (callId: {CallId})",
            pluginName, functionName, functionCall.Id);

        KernelFunction function;
        try
        {
            function = kernel.Plugins.GetFunction(pluginName, functionName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[ManualToolInvoker] Function {Plugin}.{Function} not found in kernel plugins",
                pluginName, functionName);
            return new FunctionResultContent(
                callId: functionCall.Id,
                pluginName: pluginName,
                functionName: functionName,
                result: $"{{\"$type\":\"error\",\"toolName\":\"{functionName}\",\"code\":\"FUNCTION_NOT_FOUND\",\"message\":\"Function '{functionName}' is not available.\",\"retryable\":false}}");
        }

        var arguments = new KernelArguments();
        if (functionCall.Arguments is not null)
        {
            foreach (var kvp in functionCall.Arguments)
                arguments[kvp.Key] = kvp.Value?.ToString();
        }

        var result = await kernel.InvokeAsync(function, arguments, ct);

        return new FunctionResultContent(
            callId: functionCall.Id,
            pluginName: pluginName,
            functionName: functionName,
            result: result.GetValue<object>());
    }
}
