namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// Defines the field schema and merge strategy for structured-output task inference.
/// Host applications implement this to provide domain-specific field definitions
/// so the framework can generate structured-output prompts and merge LLM responses.
/// </summary>
public interface ITaskInferenceStrategy
{
    /// <summary>
    /// The name of the primary entity being inferred.
    /// Used as the EntityType and EntityId when upserting inferred state into ContextFabric.
    /// </summary>
    string EntityName { get; }

    /// <summary>
    /// Fields for which structured output is requested from the LLM.
    /// Each field specifies its name, JSON type, constraints, and description.
    /// </summary>
    IReadOnlyList<TaskInferenceField> Fields { get; }

    /// <summary>
    /// Confidence threshold below which inferred values are discarded.
    /// Null means all inferred values are merged regardless of confidence.
    /// </summary>
    double? MinimumConfidenceThreshold { get; }
}
