namespace Affiant.Extensions.AI.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Adapters;
using Microsoft.Extensions.AI;

/// <summary>
/// The Microsoft.Extensions.AI bridge into Affiant's backend-neutral tool-invocation pipeline: a
/// <see cref="DelegatingAIFunction"/> that <em>is</em> the tool as far as every caller is concerned,
/// and runs the whole neutral filter onion (framework spec §3.12.4 canonical order: tool-error
/// wrapping, deterministic short-circuit, tracing, context extraction, argument capture, inference
/// trigger, inference merge, review gate) around the real tool body.
///
/// <para>
/// <b>Why wrapping the function, and not the client.</b> Microsoft.Extensions.AI offers two other
/// seams: <c>FunctionInvokingChatClient.FunctionInvoker</c> (a settable delegate) and a
/// <c>protected virtual InvokeFunctionAsync</c> to subclass. Both were rejected. The delegate is
/// last-write-wins and silently no-ops if a host never configures it — the exact silent-bypass class
/// Area-8 eliminated for <c>ReviewGate</c>. This wrapper cannot be bypassed that way: the wrapper IS
/// the function, so even a custom loop that calls <see cref="AIFunction.InvokeAsync"/> directly still
/// passes through Affiant. It is also byte-for-byte the mechanism the Microsoft Agent Framework uses
/// for its own function-invocation middleware (its private <c>MiddlewareEnabledFunction</c>), so this
/// adapter inherits Microsoft's own proof the mechanism works rather than needing a fresh one. See
/// the seam probe, <c>affiant-chancery/docs/overnight-mission-2026-08-20/research/meai-seam-probe.md</c>
/// §1.3–1.5, and design decision 1.
/// </para>
///
/// <para>
/// <b>How termination and result replacement reach the loop.</b>
/// <see cref="FunctionInvokingChatClient"/> publishes the live per-call
/// <see cref="FunctionInvocationContext"/> on a static <c>AsyncLocal</c>,
/// <see cref="FunctionInvokingChatClient.CurrentContext"/>, and that instance is the same object its
/// own <c>ProcessFunctionCallsAsync</c> loop later reads for the <c>Terminate</c> verdict. So setting
/// <c>Terminate</c> here stops the loop, and this method's return value is the result the caller sees.
/// That was the one claim the seam probe could not verify without running code; it is now pinned by
/// <c>Affiant.Extensions.AI.Tests.Spikes.TerminatePropagationSpikeTests</c> (design decision 2).
/// </para>
///
/// <para>
/// <b>Degraded mode.</b> A null <see cref="FunctionInvokingChatClient.CurrentContext"/> means the
/// function was invoked outside that loop — a host calling <see cref="AIFunction.InvokeAsync"/>
/// directly, or a different client implementation. The pipeline still runs in full (that is the
/// bypass-resistance the wrapping design buys); only the loop-scoped inputs and the
/// <c>Terminate</c> hand-back have nowhere to come from or go to, so they are skipped rather than
/// faked.
/// </para>
/// </summary>
public sealed class AffiantDelegatingAIFunction : DelegatingAIFunction, IAffiantWrappedFunction
{
    private readonly ToolInvocationPipeline _pipeline;
    private readonly IAffiantToolRegistry _registry;

    /// <summary>Wraps <paramref name="innerFunction"/> so every invocation runs the neutral pipeline.</summary>
    /// <param name="innerFunction">The real tool. Never invoked except as this wrapper's terminal step.</param>
    /// <param name="pipeline">The backend-neutral tool-invocation pipeline (from <c>AddAffiantCore</c>).</param>
    /// <param name="registry">
    /// The tool registry, consulted for the invoked tool's plugin name. A tool with no registered
    /// descriptor still runs — it simply carries an empty plugin name into the neutral context,
    /// matching the Microsoft Agent Framework bridge's behaviour exactly.
    /// </param>
    public AffiantDelegatingAIFunction(
        AIFunction innerFunction,
        ToolInvocationPipeline pipeline,
        IAffiantToolRegistry registry)
        : base(innerFunction)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Surfaces <see cref="DelegatingAIFunction"/>'s own protected <c>InnerFunction</c> rather than
    /// keeping a second copy of the reference, so the marker can never disagree with what the
    /// wrapper actually delegates to.
    /// </remarks>
    public AIFunction AffiantInnerFunction => InnerFunction;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // The ambient carrier FunctionInvokingChatClient populates for the call in flight. This is
        // the Microsoft.Extensions.AI analog of Semantic Kernel's Kernel.Data and of the MAF
        // middleware's FunctionInvocationContext parameter — the same object the outer loop reads
        // its Terminate verdict from.
        var context = FunctionInvokingChatClient.CurrentContext;

        var descriptor = _registry.Find(Name);

        var request = new ToolInvocationRequest(
            Name,
            descriptor?.PluginName ?? string.Empty,
            arguments)
        {
            InitialTerminate = context?.Terminate ?? false,
            TurnNumber = context?.Iteration ?? 0,
            History = context is null
                ? []
                : ExtensionsAIMessageConversions.ToNeutral(context.Messages),
            // The run's conversation identity, threaded by the host onto ChatOptions. Carrying it
            // onto the neutral context gives InferenceTriggerFilter a genuinely per-conversation
            // idempotency namespace; without it the key collapses to the fabric instance hash and
            // dedups across unrelated conversations.
            ConversationId = context?.Options?.ConversationId,
        };

        object? toolProduced = null;
        var toolRan = false;
        var downstreamTerminate = false;

        var resultContext = await _pipeline.RunAsync(
            request,
            filters => filters,
            async neutral =>
            {
                // next() IS the tool body: DelegatingAIFunction.InvokeCoreAsync forwards straight to
                // the inner function, with no intervening continuation. That is what makes
                // ToolInvocationContext.NextIsToolBody's default of true correct here (unlike
                // Semantic Kernel's completion-stage seam, which must set it false), and what makes
                // ToolErrorFilter's retry-once-on-retryable-failure safe at this seam.
                //
                // `arguments` is passed through by reference, so a pre-invocation filter's mutation
                // of neutral.Arguments — the same IDictionary instance — is what the tool actually
                // receives.
                toolProduced = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
                toolRan = true;

                // affiant#25 (same class as the SK and MAF bridges): something downstream of us —
                // another wrapping layer, or the host's own function-invocation configuration — can
                // set Terminate on the shared context before returning. Capture it now so the
                // neutral filters' own Terminate verdict below cannot silently discard it.
                downstreamTerminate = context?.Terminate ?? false;

                neutral.Result = toolProduced;

                // Area-3 P2 ruling 3: mark the tool as executed the instant it succeeds, before any
                // wrapping filter's post-next() logic (TaskInferenceMergeFilter, ReviewGateFilter,
                // host ContextExtractor subclasses) runs on this single onion — governs
                // ToolErrorFilter's tool-body vs. post-processing catch decision.
                neutral.ToolExecuted = true;
            },
            // Prefer the invocation's ambient scope when the host wired one onto the function
            // arguments; otherwise the pipeline owns a fresh scope per invocation, giving each tool
            // call its own conversation fabric (concurrent runs never share fabric state).
            arguments.Services,
            cancellationToken).ConfigureAwait(false);

        // affiant#25: OR, never overwrite. Handing Terminate back is what stops the chat loop —
        // proven end-to-end by TerminatePropagationSpikeTests.
        if (context is not null)
            context.Terminate = resultContext.Terminate || downstreamTerminate;

        // Return the neutral result whenever a filter replaced it (ReviewGateFilter's turn-ending
        // message, DeterministicShortCircuit's precomputed answer, an error envelope), and the
        // tool's own object otherwise — identity-compared so a filter that legitimately sets
        // neutral.Result to the very same object is not mistaken for a replacement.
        return !toolRan || !ReferenceEquals(resultContext.Result, toolProduced)
            ? resultContext.Result
            : toolProduced;
    }
}
