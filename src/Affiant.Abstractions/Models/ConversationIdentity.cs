namespace Affiant.Abstractions.Models;

/// <summary>
/// Immutable record representing the identity context of a conversation.
/// Used for session tracking and multi-tenant scenarios (Phase 3).
/// </summary>
public record ConversationIdentity(
    string SessionId,
    string UserId,
    DateTimeOffset StartedAt,
    string? HostAppName = null);
