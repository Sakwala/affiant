using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Affiant.SemanticKernel.Connectors;

// Degraded/fallback invocation path for providers without SK's native auto-function-invocation
// loop. kernel.InvokeAsync fires only the invocation-stage bridge (IFunctionInvocationFilter); the
// completion stage (merge + review gate) lives at SK's IAutoFunctionInvocationFilter position, which
// only the auto-invocation loop drives — so this path must run the completion segment explicitly, or
// a manually-invoked write tool's WriteProposal is never filed for review.
public class ManualToolInvoker(ToolInvocationPipeline pipeline, ILogger<ManualToolInvoker> logger)
    : IManualToolInvoker
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

            // Area-3 P2 fix round (finding 2 scoping correction): FUNCTION_NOT_FOUND is a FRAMEWORK
            // code — the original P2 wave wrongly grouped this literal with host-side adoption and
            // deferred it. Built through the real ToolError type + ToolErrorCodes.FunctionNotFound,
            // not a hand-written JSON string, so it is caught by the source-scan lock
            // (AssertToolErrorCodeSourceScanTests) like every other framework emission site.
            var notFound = new ToolError(
                ToolName: functionName,
                Timestamp: DateTimeOffset.UtcNow,
                Code: ToolErrorCodes.FunctionNotFound,
                Message: $"Function '{functionName}' is not available.",
                Retryable: false);

            return new FunctionResultContent(
                callId: functionCall.Id,
                pluginName: pluginName,
                functionName: functionName,
                result: notFound.ToJsonString());
        }

        var arguments = new KernelArguments();
        if (functionCall.Arguments is not null)
        {
            foreach (var kvp in functionCall.Arguments)
                arguments[kvp.Key] = kvp.Value?.ToString();
        }

        var result = await kernel.InvokeAsync(function, arguments, ct);
        var produced = result.GetValue<object>();

        // Run the completion segment (TaskInferenceMergeFilter → ReviewGateFilter) over the tool
        // result, mirroring AffiantAutoFunctionInvocationBridge. No double-filing risk: the manual
        // and auto paths are mutually exclusive by design (manual runs only when the provider lacks
        // native auto-invocation), and kernel.InvokeAsync above never drives the completion stage.
        var completionRequest = new ToolInvocationRequest(
            functionName, pluginName, new Dictionary<string, object?>());

        var completed = await pipeline.RunAsync(
            completionRequest,
            BridgeStages.CompletionStage,
            neutral =>
            {
                neutral.Result = produced;
                // Area-3 P2 ruling 3: the tool already ran (kernel.InvokeAsync above) before this
                // completion-stage pipeline call even starts — so ToolErrorFilter (now also part of
                // BridgeStages.CompletionStage, ruling 1) must treat any exception from the
                // completion-stage filters as post-processing, never as a retryable tool-body
                // failure that would invoke kernel.InvokeAsync a second time.
                neutral.ToolExecuted = true;
                return Task.CompletedTask;
            },
            // Same kernel scope as the invocation stage fired by kernel.InvokeAsync above, so the
            // completion stage sees the conversation fabric the tool call populated.
            kernel.Services,
            ct);

        return new FunctionResultContent(
            callId: functionCall.Id,
            pluginName: pluginName,
            functionName: functionName,
            result: completed.Result);
    }
}
