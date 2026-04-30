namespace Affiant.Abstractions.Interfaces;

/// <summary>
/// Deterministic short-circuit for high-failure-cost intents.
/// Evaluated before LLM invocation; if MatchesAsync returns true, HandleAsync
/// provides the response directly without touching the model.
/// </summary>
public interface IIntentInterceptor
{
    Task<bool> MatchesAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    Task<object?> HandleAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);
}
