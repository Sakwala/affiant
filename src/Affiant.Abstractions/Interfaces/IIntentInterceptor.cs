namespace Affiant.Abstractions.Interfaces;

using Microsoft.SemanticKernel;

/// <summary>
/// Deterministic short-circuit for high-failure-cost intents.
/// Evaluated before LLM invocation; if ShouldIntercept returns true, HandleAsync
/// provides the response directly without touching the model.
/// </summary>
public interface IIntentInterceptor
{
    bool ShouldIntercept(string functionName, Dictionary<string, object?> arguments);
    Task<FunctionResult> HandleAsync(string functionName, Dictionary<string, object?> arguments);
}
