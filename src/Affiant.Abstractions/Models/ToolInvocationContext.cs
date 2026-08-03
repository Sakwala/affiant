namespace Affiant.Abstractions.Models;

/// <summary>
/// Backend-neutral per-invocation context that the framework's tool-interception pipeline
/// operates on. Each interception backend (Semantic Kernel, Microsoft Agent Framework)
/// translates its native invocation context into this shape, runs the neutral pipeline, and
/// translates the outcome back at its own edge.
/// </summary>
public sealed class ToolInvocationContext
{
    public required string FunctionName { get; init; }

    public required string PluginName { get; init; }

    /// <summary>LLM-supplied tool arguments. Mutable pre-invocation.</summary>
    public required IDictionary<string, object?> Arguments { get; init; }

    /// <summary>
    /// The produced tool result. Readable and replaceable post-invocation. The backend bridge
    /// makes the final value the result its framework reports (SK: the context result; MAF: the
    /// middleware delegate return value).
    /// </summary>
    public object? Result { get; set; }

    /// <summary>Requests the backend stop the auto-invocation loop after this call.</summary>
    public bool Terminate { get; set; }

    /// <summary>
    /// Set by the bridge/middleware's terminal delegate the instant the real tool call finishes
    /// (successfully) — before any filter's post-<c>next()</c> work runs. Distinguishes "tool body"
    /// failures (a real tool call, or a pre-tool filter such as <c>DeterministicShortCircuit</c>,
    /// threw — <see cref="ToolExecuted"/> is still <see langword="false"/>, retrying is safe because
    /// the tool has not produced a result yet) from "post-processing" failures (a filter that only
    /// runs after the tool already returned — e.g. a <c>ContextExtractor</c> subclass or
    /// <c>TaskInferenceMergeFilter</c> — threw; <see cref="ToolExecuted"/> is <see langword="true"/>,
    /// so <see cref="Result"/> already holds the tool's genuine output and must never be discarded
    /// or retried into a second tool execution). See <c>ToolErrorFilter</c>'s remarks (area-3 P2
    /// ruling 3) for how this flag governs its catch/retry decision.
    /// </summary>
    public bool ToolExecuted { get; set; }

    /// <summary>
    /// Declares what re-invoking <c>next(context)</c> actually re-runs at THIS seam. Default
    /// <see langword="true"/>: <c>next()</c> IS the tool body (or the remainder of the onion
    /// leading directly to it) — MAF's single onion and the core pipeline's terminal both satisfy
    /// this, so a retry there only ever re-executes the real tool, never anything more.
    ///
    /// <para>
    /// <b>Why this exists (area-3 P2 fix round, corrects the disproven "structurally impossible"
    /// claim from ruling 1).</b> SK's completion-stage seam
    /// (<c>Affiant.SemanticKernel.Filters.AffiantAutoFunctionInvocationBridge</c>) is the one place
    /// this is <see langword="false"/>: its terminal's <c>next(context)</c> is SK's OWN
    /// auto-invocation continuation, not the tool — that continuation nested-invokes the real tool
    /// through a SEPARATE <c>ToolInvocationContext</c> at the invocation-stage seam. A real
    /// scenario this must guard against: a host-registered SK filter that runs outside Affiant's
    /// bridges (or SK's own argument-binding step) throws before the nested invocation-stage call
    /// even happens. At that point <see cref="ToolExecuted"/> is still <see langword="false"/> (the
    /// tool never got a chance to run), so without this flag <c>ToolErrorFilter</c> would classify
    /// it as a genuine tool-body failure and retry by calling <c>next(context)</c> a SECOND time —
    /// which calls SK's continuation again, genuinely re-executing the tool for a failure that had
    /// nothing to do with it. Two independent adversarial refuters reproduced exactly this
    /// (<c>nextCallCount == 2</c>) against the ruling-1 implementation that relied on
    /// <see cref="ToolExecuted"/> alone.
    /// </para>
    ///
    /// <para>
    /// <c>ManualToolInvoker</c>'s completion-stage terminal does NOT need this set to
    /// <see langword="false"/>: it sets <see cref="ToolExecuted"/> synchronously before anything
    /// that can throw, and its terminal takes no <c>next()</c> of its own (the tool already ran via
    /// <c>kernel.InvokeAsync</c> before the completion-stage pipeline call even starts) — so a
    /// retry there is a harmless idempotent re-run of the terminal's own assignment, not a second
    /// real tool execution. See <c>ToolErrorFilter</c>'s remarks for how this flag governs its
    /// retry decision alongside <see cref="ToolExecuted"/>.
    /// </para>
    /// </summary>
    public bool NextIsToolBody { get; set; } = true;

    /// <summary>The per-invocation DI scope owned by the pipeline runner.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Ambient conversation identifier, populated by the backend bridge. May be null.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Ambient turn counter, populated by the backend bridge.</summary>
    public int TurnNumber { get; init; }

    /// <summary>Ambient conversation history, populated by the backend bridge.</summary>
    public IReadOnlyList<AffiantChatMessage> History { get; init; } = [];
}
