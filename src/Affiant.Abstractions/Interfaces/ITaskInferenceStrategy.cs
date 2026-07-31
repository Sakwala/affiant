namespace Affiant.Abstractions.Interfaces;

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

/// <summary>
/// Describes a single field in the structured-output schema used for task inference.
/// </summary>
/// <param name="Format">
/// An explicit semantic hint for how to render/validate the field (e.g. "date").
/// Optional and additive: <c>SchemaDrivenAffidavitProjection</c> uses it to derive
/// <c>AffidavitField.Kind</c> "date" when <see cref="JsonType"/> alone is ambiguous
/// (a "string" JsonType could be free text or a date — guessing from <see cref="Pattern"/>
/// would be unreliable, so this is the explicit signal instead). Null means no hint;
/// the projection falls back to <see cref="JsonType"/>-based inference.
/// </param>
public record TaskInferenceField(
    string Name,
    string JsonType,
    string Description,
    int? MaxLength = null,
    string? Pattern = null,
    IReadOnlyList<string>? Enum = null,
    bool Required = false,
    string? Format = null);
