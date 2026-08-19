namespace Affiant.Abstractions.Models;

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
/// <param name="Projected">
/// Whether this field is surfaced on the Evidence Card as an <c>AffidavitField</c>. Defaults
/// to <c>true</c> so every existing construction site compiles unchanged and keeps its current
/// behavior. Set to <c>false</c> to declare an <b>extraction field</b>: a field the LLM is still
/// asked to extract (both inference ports send every field regardless of this flag — extraction
/// fields exist precisely to be extracted) and that is still merged into <c>ContextFabric</c> by
/// <c>TaskInferenceStep</c>, but that never appears in the projected <c>Affidavit.Fields</c>.
/// Its extracted value and provenance are instead exposed to <c>IFieldResolver</c>
/// implementations as an <c>ExtractionFact</c> via <c>FieldResolutionContext.Facts</c> — useful
/// for values a resolver needs as an input (e.g. a tail number mentioned in conversation) without
/// that raw value itself ever reaching the reviewer-facing card. Combining <c>Projected: false</c>
/// with <see cref="Required"/><c>: true</c> is invalid and rejected at projection construction
/// time — an extraction fact that never becomes a card field cannot gate the card.
/// </param>
public record TaskInferenceField(
    string Name,
    string JsonType,
    string Description,
    int? MaxLength = null,
    string? Pattern = null,
    IReadOnlyList<string>? Enum = null,
    bool Required = false,
    string? Format = null,
    bool Projected = true);
