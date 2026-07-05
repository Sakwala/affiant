namespace Affiant.SemanticKernel.Filters;

using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

/// <summary>
/// Semantic Kernel bridge for the invocation-stage segment of the neutral tool-invocation
/// pipeline. Fires at SK's <see cref="IFunctionInvocationFilter"/> position — for every function
/// invocation, including manual <c>kernel.InvokeAsync</c> — so all pre-tool filters (error
/// wrapping, deterministic short-circuit, tracing, context extraction, argument capture,
/// inference trigger) run exactly where SK's function-invocation filters ran before.
///
/// The completion-stage filters (<see cref="TaskInferenceMergeFilter"/>, <see cref="ReviewGateFilter"/>)
/// run at the auto-invocation position instead — see <see cref="AffiantAutoFunctionInvocationBridge"/>.
/// This bridge deliberately excludes them.
/// </summary>
public sealed class AffiantFunctionInvocationBridge(ToolInvocationPipeline pipeline)
    : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        var request = new ToolInvocationRequest(
            context.Function.Name,
            context.Function.PluginName ?? string.Empty,
            context.Arguments ?? new KernelArguments())
        {
            ConversationId = ReadConversationId(context.Kernel),
            TurnNumber = ReadTurnNumber(context.Kernel),
            History = ReadHistory(context.Kernel),
        };

        object? toolProduced = null;
        var toolRan = false;

        var resultContext = await pipeline.RunAsync(
            request,
            BridgeStages.InvocationStage,
            async neutral =>
            {
                await next(context);
                toolRan = true;
                toolProduced = context.Result?.GetValue<object>();
                neutral.Result = toolProduced;
            },
            // Run every filter (and the conversation-scoped fabric) in the kernel's own scope so the
            // invocation and auto-invocation stages of one turn share a single fabric instance, and
            // concurrent turns (distinct kernel scopes) stay isolated.
            context.Kernel.Services,
            context.CancellationToken).ConfigureAwait(false);

        // Write the (possibly replaced) neutral result back onto the SK context. Skip the rewrap
        // when the tool ran and no filter changed the value, preserving the tool's FunctionResult.
        if (!toolRan || !ReferenceEquals(resultContext.Result, toolProduced))
        {
            context.Result = new FunctionResult(context.Function, resultContext.Result);
        }
    }

    private static string? ReadConversationId(Kernel kernel) =>
        kernel.Data.TryGetValue("ConversationId", out var cid) && cid is string s && !string.IsNullOrEmpty(s)
            ? s
            : null;

    private static int ReadTurnNumber(Kernel kernel)
    {
        if (!kernel.Data.TryGetValue("AffiantTurnNumber", out var tn)) return 0;
        return tn switch
        {
            int i => i,
            string str when int.TryParse(str, out var parsed) => parsed,
            _ => 0
        };
    }

    private static IReadOnlyList<AffiantChatMessage> ReadHistory(Kernel kernel) =>
        kernel.Data.TryGetValue("ChatHistory", out var histObj) && histObj is ChatHistory history
            ? SkMessageConversions.ToNeutral(history)
            : [];
}
