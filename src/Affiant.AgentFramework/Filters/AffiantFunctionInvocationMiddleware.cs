namespace Affiant.AgentFramework.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.AgentFramework.Adapters;
using Affiant.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// MAF bridge for the backend-neutral tool-invocation pipeline. MAF exposes exactly one
/// function-calling seam (unlike Semantic Kernel's two-position split — see
/// <c>Affiant.SemanticKernel.Filters.BridgeStages</c>), so every neutral filter runs here in
/// canonical order (framework spec §3.12.4): tool-error wrapping, context extraction, argument
/// capture, inference trigger, deterministic short-circuit, inference merge, review gate.
///
/// Seals evidence by returning <see cref="ToolInvocationContext.Result"/> from the middleware
/// delegate — <see cref="FunctionInvocationContext"/> has no settable <c>.Result</c> (proposal §2).
/// A neutral <c>Terminate</c> maps to <see cref="FunctionInvocationContext.Terminate"/>.
/// </summary>
public sealed class AffiantFunctionInvocationMiddleware(
    ToolInvocationPipeline pipeline,
    IAffiantToolRegistry registry)
{
    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(context.Function.Name);

        var request = new ToolInvocationRequest(
            context.Function.Name,
            descriptor?.PluginName ?? string.Empty,
            context.Arguments)
        {
            InitialTerminate = context.Terminate,
            TurnNumber = context.Iteration,
            History = MafMessageConversions.ToNeutral(context.Messages),
            // MAF threads the run's conversation identity onto ChatOptions.ConversationId (set by the
            // host on the agent thread / run options). Carrying it onto the neutral context gives
            // InferenceTriggerFilter a genuinely per-conversation idempotency namespace; without it the
            // key collapses to the fabric instance hash and dedups across unrelated conversations.
            ConversationId = context.Options?.ConversationId,
        };

        object? toolProduced = null;
        var toolRan = false;

        var resultContext = await pipeline.RunAsync(
            request,
            filters => filters,
            async neutral =>
            {
                toolProduced = await next(context, cancellationToken).ConfigureAwait(false);
                toolRan = true;
                neutral.Result = toolProduced;
            },
            // Prefer the run's ambient scope when the host wired one onto the function arguments;
            // otherwise the pipeline owns a fresh scope per invocation, giving each tool call its own
            // conversation fabric (concurrent MAF runs never share fabric state).
            context.Arguments?.Services,
            cancellationToken).ConfigureAwait(false);

        context.Terminate = resultContext.Terminate;

        return !toolRan || !ReferenceEquals(resultContext.Result, toolProduced)
            ? resultContext.Result
            : toolProduced;
    }
}
