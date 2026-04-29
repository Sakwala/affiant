using System.Text.Json.Serialization;

namespace Affiant.Abstractions.Models;

/// <summary>
/// The provenance sources an <see cref="ProvenanceTag"/> field can originate from.
///
/// Values are ordered from most deterministic to least. This ordering doubles as the
/// determinism hierarchy for confidence-tie merge rules: when two tags carry equal
/// confidence, the one with the lower integer ordinal wins.
///
/// Full rationale lives in the framework specification §2.1.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProvenanceSource
{
    /// <summary>
    /// The user explicitly stated this value. Maximal confidence.
    /// </summary>
    UserStated,

    /// <summary>
    /// Fetched from an authoritative external system (API lookup, database read,
    /// third-party service response).
    /// </summary>
    External,

    /// <summary>
    /// Derived by deterministic business logic (tax calculation, date math,
    /// priority-based SLA computation).
    /// </summary>
    Computed,

    /// <summary>
    /// Mentioned in conversation context through a tool result but not directly
    /// stated as a value by the user.
    /// </summary>
    Conversation,

    /// <summary>
    /// LLM-inferred from conversational signals. Requires reviewer confirmation
    /// before committing.
    /// </summary>
    Inferred,

    /// <summary>
    /// System default or fallback applied when no conversational basis exists.
    /// </summary>
    Default,

    /// <summary>
    /// Provenance unknown. MUST be explicitly tagged rather than omitted —
    /// missing provenance is indistinguishable from "the framework forgot to
    /// track it". See framework spec §6 Rule 7.
    /// </summary>
    Empty
}

/// <summary>
/// Sworn-provenance tag carried by every field value the framework tracks.
/// Records where a value came from (<see cref="Source"/>), how confident
/// the framework is in it (<see cref="Confidence"/>), a human-readable
/// explanation (<see cref="Evidence"/>), and which conversation turn produced it
/// (<see cref="ConversationTurn"/>).
///
/// Matches framework specification §2.2.
/// </summary>
public sealed record ProvenanceTag(
    ProvenanceSource Source,
    float Confidence,
    string? Evidence,
    int? ConversationTurn)
{
    /// <summary>
    /// The canonical "no data" tag. Every field with no known provenance must be
    /// tagged with this value rather than left unset — see framework spec §6 Rule 7.
    /// </summary>
    public static ProvenanceTag Empty { get; } = new(ProvenanceSource.Empty, 0f, null, null);

    /// <summary>
    /// Tag a value extracted from a deterministic tool result. Default confidence 0.9.
    /// </summary>
    public static ProvenanceTag FromTool(string toolName, float confidence = 0.9f) =>
        new(ProvenanceSource.Conversation, confidence, $"Extracted from {toolName}", null);

    /// <summary>
    /// Tag an LLM-inferred field. Default confidence 0.6.
    /// </summary>
    public static ProvenanceTag FromInference(string fieldName, float confidence = 0.6f) =>
        new(ProvenanceSource.Inferred, confidence, $"LLM inferred: {fieldName}", null);

    /// <summary>
    /// Tag a value applied by a deterministic fallback rule. Default confidence 0.3.
    /// </summary>
    public static ProvenanceTag FromDefault(string reason, float confidence = 0.3f) =>
        new(ProvenanceSource.Default, confidence, reason, null);

    /// <summary>
    /// Tag a value the user stated directly in chat. Maximal confidence.
    /// </summary>
    public static ProvenanceTag FromUser(string fieldName) =>
        new(ProvenanceSource.UserStated, 1.0f, $"User stated: {fieldName}", null);
}
