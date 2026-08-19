namespace Affiant.Abstractions.Models;

/// <summary>
/// Identity and lifecycle timestamps for one chat session, as persisted by
/// <see cref="Interfaces.IChatSessionStore"/>. Carries no messages — the message list is stored
/// and loaded separately so a session's identity can be read without materializing its transcript.
/// </summary>
public sealed record ChatSession(
    string SessionId,
    string TenantId,
    string UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);
