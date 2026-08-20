namespace Affiant.Extensions.AI.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
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
///
/// <para>
/// <b>Re-entrancy guard.</b> <c>WithAffiant</c>'s wire-up marker check inspects only the top-level
/// type of each tool, so one layer of host middleware between <see cref="ChatOptions.Tools"/> and
/// this wrapper (telemetry, retry, redaction, argument coercion — and that is exactly the shape the
/// Microsoft Agent Framework itself uses) hides the marker and lets a second <c>WithAffiant</c>
/// succeed. This wrapper therefore also refuses at <em>invoke</em> time: an ambient
/// <c>AsyncLocal</c> records that an onion is already running for the call in flight, and a nested
/// wrapper entered under the same <see cref="FunctionInvocationContext"/> throws instead of running
/// the onion a second time. That catches every nesting shape the marker cannot see, including
/// <c>Affiant.AgentFramework</c> wrapped over the same tools. Pinned by
/// <c>Affiant.Extensions.AI.Tests.Filters.NestedWrapperReentrancyTests</c>; see also
/// <c>ChatOptionsExtensions</c> and the package README's "One Affiant adapter per tool catalog".
/// </para>
/// </summary>
public sealed class AffiantDelegatingAIFunction : DelegatingAIFunction, IAffiantWrappedFunction
{
    /// <summary>
    /// Records that an Affiant onion is already running on this execution context, and which
    /// <see cref="FunctionInvocationContext"/> it belongs to. Assigned inside an <c>async</c> method,
    /// so the write is confined to the invocation that made it: it flows forward into the tool body
    /// (where a nested wrapper will see it) but never back out to the caller and never sideways into
    /// a concurrently invoked sibling under
    /// <c>FunctionInvokingChatClient.AllowConcurrentInvocation</c>.
    /// </summary>
    private static readonly AsyncLocal<ActiveOnion?> RunningOnion = new();

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

        // Invoke-time half of the double-wrap guard (see the type's "Re-entrancy guard" remarks).
        // Reference equality on the ambient FunctionInvocationContext is what separates the two
        // shapes that look alike from in here:
        //
        //   * Double-wrap — two Affiant wrappers nested around ONE logical tool call, whatever sits
        //     between them. Nothing between them starts a new chat loop, so both read the same
        //     CurrentContext instance and the two are reference-equal. Refuse: a second onion would
        //     double-tag provenance, fire task inference twice, and file the same write proposal
        //     onto the docket twice.
        //   * A tool body that legitimately runs a nested agent whose own tools are Affiant-governed.
        //     The inner FunctionInvokingChatClient publishes a FRESH FunctionInvocationContext, so
        //     the instances differ and the inner onion is allowed to run — it is a genuinely
        //     different logical tool call, not this one being processed twice.
        //
        // Both-null (nested direct AIFunction.InvokeAsync calls with no loop anywhere) is treated as
        // the first case and refused: outside a loop there is no signal separating them, and this
        // guard fails closed. A host that really wants a nested governed call from inside a tool body
        // should run it through its own FunctionInvokingChatClient.
        var outerOnion = RunningOnion.Value;
        if (outerOnion is not null && ReferenceEquals(outerOnion.Context, context))
            throw new InvalidOperationException(
                $"Affiant.Extensions.AI: the tool '{Name}' is wrapped by Affiant twice, so one tool " +
                "call would run the neutral filter onion twice — double-tagging provenance, firing " +
                "task inference twice, and filing the same write proposal onto the docket twice. " +
                "This invocation was refused instead. WithAffiant's wire-up check could not see the " +
                "second wrapper because something sits between ChatOptions.Tools and it — a host " +
                "DelegatingAIFunction (telemetry, retry, redaction, argument coercion), or " +
                "Affiant.AgentFramework's own per-run wrapper. Call WithAffiant exactly once per " +
                "ChatOptions, on the unwrapped catalog, use only the ChatOptions it returns, and " +
                "never wire a second Affiant adapter over tools this one already governs.");

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
            // idempotency namespace.
            //
            // Hosts that leave ChatOptions.ConversationId null get the degenerate case, and it is
            // silent: the idempotency key falls back to the shared ContextFabric's identity hash
            // (see the KNOWN LIMITATION note on the ambient provider below), so every conversation
            // after the first is treated as a repeat and its write-tool inference is skipped
            // entirely. Set ConversationId per conversation.
            ConversationId = context?.Options?.ConversationId,
        };

        object? toolProduced = null;
        var toolRan = false;
        var downstreamTerminate = false;

        // Publish the re-entrancy record for the duration of the onion, so a nested Affiant wrapper
        // reached through the tool body sees it. Restored rather than cleared in the finally, so a
        // nested agent's own inner onion cannot strip the outer one's record on its way out.
        var previousOnion = RunningOnion.Value;
        RunningOnion.Value = new ActiveOnion(context);

        ToolInvocationContext resultContext;
        try
        {
            resultContext = await _pipeline.RunAsync(
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
                // The invocation's ambient provider, when the host wired one onto the function
                // arguments; the pipeline falls back to a scope of its own when this is null.
                //
                // KNOWN LIMITATION at this seam. FunctionInvokingChatClient sets
                // AIFunctionArguments.Services to the provider the ChatClientBuilder was built from — in
                // the documented wiring, the application ROOT provider. So this is virtually never null
                // here, the pipeline's own per-invocation scope branch is effectively unreachable, and
                // the scoped ContextFabric resolves to one process-global instance shared by every
                // conversation. Two consequences, both pinned by
                // Filters/ConversationScopeBleedAtTheSeamTests: InferenceTriggerFilter's idempotency key
                // falls back to that fabric's identity hash when ConversationId above is null, so the
                // second and every later conversation silently skips write-tool inference; and
                // ToolArgumentCaptureFilter's provenance chains, keyed on the bare argument name, are
                // overwritten across conversations. Setting ChatOptions.ConversationId fixes the first,
                // and the README and WithAffiant both say so. The real fix — a per-turn scope, or
                // namespacing provenance by conversation — is framework-wide: Affiant.AgentFramework's
                // AffiantFunctionInvocationMiddleware and Affiant.SemanticKernel's
                // AffiantFunctionInvocationBridge source their provider identically and share the
                // defect. Tracked for a post-beta wave, not fixed inside this adapter.
                arguments.Services,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RunningOnion.Value = previousOnion;
        }

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

    /// <summary>
    /// The ambient record of an onion in flight: which <see cref="FunctionInvocationContext"/> it is
    /// running for, so a nested wrapper can tell "this same logical tool call, wrapped twice" (the
    /// same instance, including both being null) from "a different tool call started inside the tool
    /// body" (a nested chat loop published its own instance).
    /// </summary>
    private sealed class ActiveOnion(FunctionInvocationContext? context)
    {
        public FunctionInvocationContext? Context { get; } = context;
    }
}
