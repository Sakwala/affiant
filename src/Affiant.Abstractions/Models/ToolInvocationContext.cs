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

    /// <summary>The per-invocation DI scope owned by the pipeline runner.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Ambient conversation identifier, populated by the backend bridge. May be null.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Ambient turn counter, populated by the backend bridge.</summary>
    public int TurnNumber { get; init; }

    /// <summary>Ambient conversation history, populated by the backend bridge.</summary>
    public IReadOnlyList<AffiantChatMessage> History { get; init; } = [];
}
