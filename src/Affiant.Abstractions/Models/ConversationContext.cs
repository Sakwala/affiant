namespace Affiant.Abstractions.Models;

/// <summary>
/// Domain-agnostic accumulated entity state for a session.
/// Hosts encode domain-specific data (e.g., aircraft, parts) as EntityRef objects
/// in the Entities dictionary. Keys are stable entity identifiers.
/// </summary>
public sealed record ConversationContext(
    string SessionId,
    Dictionary<string, EntityRef> Entities);
