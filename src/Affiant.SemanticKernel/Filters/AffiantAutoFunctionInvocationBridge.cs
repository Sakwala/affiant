namespace Affiant.SemanticKernel.Filters;

using Affiant.Core.Services;
using Microsoft.SemanticKernel;

/// <summary>
/// Semantic Kernel bridge for the completion-stage segment of the neutral tool-invocation
/// pipeline. Fires at SK's <see cref="IAutoFunctionInvocationFilter"/> position — the auto-
/// invocation loop, where SK exposes result replacement and loop termination — running the
/// completion-stage filters (<see cref="Affiant.Core.Filters.TaskInferenceMergeFilter"/> then
/// <see cref="Affiant.Core.Filters.ReviewGateFilter"/>) exactly where SK's auto-function-invocation
/// filters ran before.
///
/// A neutral filter's <c>Terminate</c> maps to <see cref="AutoFunctionInvocationContext.Terminate"/>;
/// a replaced <c>Result</c> is written back to the SK context.
/// </summary>
public sealed class AffiantAutoFunctionInvocationBridge(ToolInvocationPipeline pipeline)
    : IAutoFunctionInvocationFilter
{
    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context,
        Func<AutoFunctionInvocationContext, Task> next)
    {
        // Completion-stage filters (merge, review gate) key off the result, function identity, and
        // termination — not the arguments. AutoFunctionInvocationContext.Arguments can also throw
        // when the loop did not supply KernelArguments, so we deliberately do not read it here.
        var request = new ToolInvocationRequest(
            context.Function.Name,
            context.Function.PluginName ?? string.Empty,
            new Dictionary<string, object?>())
        {
            InitialTerminate = context.Terminate,
        };

        object? toolProduced = null;
        var toolRan = false;

        var resultContext = await pipeline.RunAsync(
            request,
            BridgeStages.CompletionStage,
            async neutral =>
            {
                await next(context);
                toolRan = true;
                toolProduced = context.Result?.GetValue<object>();
                neutral.Result = toolProduced;
                // Area-3 P2 ruling 3/1: by the time this terminal returns, the real tool call (which
                // happens inside `next(context)`, nested through the invocation-stage bridge/onion)
                // has already completed. Marking it here — before ReviewGateFilter/
                // TaskInferenceMergeFilter's own post-next() logic runs — means a completion-stage
                // filter's own failure is always classified as post-processing by ToolErrorFilter's
                // ToolExecuted-gated catch, never as a retryable tool-body failure that would
                // re-execute the tool a second time.
                neutral.ToolExecuted = true;
            },
            // Same kernel scope as the invocation stage — see AffiantFunctionInvocationBridge — so the
            // completion-stage merge writes to, and the review gate reads, the same conversation fabric.
            context.Kernel.Services,
            context.CancellationToken).ConfigureAwait(false);

        if (!toolRan || !ReferenceEquals(resultContext.Result, toolProduced))
        {
            context.Result = new FunctionResult(context.Function, resultContext.Result);
        }

        context.Terminate = resultContext.Terminate;
    }
}
