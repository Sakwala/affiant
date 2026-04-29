namespace Affiant.Abstractions.Models;

/// <summary>
/// Entity reference extracted from tool results, used by context extraction filters
/// to build the conversation context. The entity type and fields are domain-agnostic:
/// the type is stored as a string name and data as flat key-value pairs.
///
/// Matches framework specification §2.5.
/// </summary>
public sealed record EntityRef(
    string EntityType,
    string EntityId,
    string DisplayName,
    Dictionary<string, object> Fields);
