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
        // affiant#114: the review gate runs here, at the completion stage, and it is the seam that
        // attaches the call's arguments to a WriteProposal — they are part of the entry-id material
        // (GT-4). Passing an empty set gave SK a different id from every other backend for the same
        // logical proposal, and gave two SK calls that differ only in their arguments the same id.
        var request = new ToolInvocationRequest(
            context.Function.Name,
            context.Function.PluginName ?? string.Empty,
            ReadArguments(context))
        {
            InitialTerminate = context.Terminate,
            // Area-3 P2 fix round (corrects the disproven "structurally impossible" claim from
            // ruling 1): this seam's next() below is SK's OWN auto-invocation continuation, not the
            // tool — it nested-invokes the real tool through a SEPARATE ToolInvocationContext at
            // the invocation-stage seam. If that continuation throws before the tool runs (a
            // host-registered SK filter outside Affiant's bridges, or SK argument-binding, failing
            // pre-tool), ToolExecuted is still false — without this flag ToolErrorFilter would
            // retry by calling next() a second time, genuinely re-executing the tool for a failure
            // that had nothing to do with it. See ToolInvocationContext.NextIsToolBody's remarks.
            InitialNextIsToolBody = false,
        };

        object? toolProduced = null;
        var toolRan = false;
        var downstreamTerminate = false;

        var resultContext = await pipeline.RunAsync(
            request,
            BridgeStages.CompletionStage,
            async neutral =>
            {
                await next(context);
                toolRan = true;
                // affiant#25: next(context) above is SK's OWN remaining auto-invocation chain — any
                // IAutoFunctionInvocationFilter a host or the framework registers AFTER this bridge
                // runs nested inside that call and can set context.Terminate = true for its own
                // reasons before returning control here. Capture that decision now, before the
                // neutral completion-stage filters (TaskInferenceMergeFilter, ReviewGateFilter) get
                // a chance to have their own (unrelated) Terminate verdict overwrite it below.
                downstreamTerminate = context.Terminate;
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

        // affiant#25: OR, never overwrite — either side (a downstream SK filter, or Affiant's own
        // completion-stage filters) can independently want the turn to end, and either verdict must
        // survive. Prior code unconditionally assigned resultContext.Terminate here, silently
        // discarding a downstream filter's Terminate=true whenever the neutral pipeline itself had
        // no opinion — this forced a host workaround of calling
        // kernel.AutoFunctionInvocationFilters.Insert(0, ...) (running its filter BEFORE this
        // bridge instead of after, the normal position).
        context.Terminate = resultContext.Terminate || downstreamTerminate;
    }

    /// <summary>
    /// The arguments SK holds for this call, or an empty set when it holds none this bridge can read.
    /// </summary>
    /// <remarks>
    /// <see cref="AutoFunctionInvocationContext.Arguments"/> is documented to throw
    /// <see cref="InvalidOperationException"/> when the auto-invocation loop's arguments are not a
    /// <see cref="KernelArguments"/>, so the read is guarded rather than skipped. The empty set is
    /// what this bridge passed unconditionally before affiant#114, so a loop that cannot hand its
    /// arguments over is no worse off than it was.
    /// <para>
    /// What comes back is Semantic Kernel's own live <see cref="KernelArguments"/> for this call, not
    /// a copy: SK's auto-invocation loop invokes the function with that same instance after every
    /// completion-stage filter's pre-<c>next</c> code has run, so a host filter that writes to
    /// <c>ToolInvocationContext.Arguments</c> here changes what the tool executes with — the same
    /// contract the invocation seam already states (<c>ToolInvocationContext</c>: "Mutable
    /// pre-invocation"). Before affiant#114 this stage's dictionary was a throwaway and such a write
    /// was inert.
    /// </para>
    /// </remarks>
    private static IDictionary<string, object?> ReadArguments(AutoFunctionInvocationContext context)
    {
        try
        {
            var arguments = context.Arguments;
            if (arguments is not null)
                return arguments;
        }
        catch (InvalidOperationException)
        {
            // Falls through to the empty set below.
        }

        return new Dictionary<string, object?>();
    }
}
