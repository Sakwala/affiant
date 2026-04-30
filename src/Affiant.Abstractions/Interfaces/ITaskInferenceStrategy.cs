namespace Affiant.Abstractions.Interfaces;

public interface ITaskInferenceStrategy
{
    /// <summary>
    /// Merge fields inferred by the LLM's structured output into the conversation context.
    /// Only fields meeting the confidence threshold are applied.
    /// Returns an updated context — no side effects.
    /// </summary>
    Task<ConversationContext> MergeInferredFieldsAsync(
        Dictionary<string, object?> inferredFields,
        ConversationContext currentContext,
        float confidenceThreshold);
}
