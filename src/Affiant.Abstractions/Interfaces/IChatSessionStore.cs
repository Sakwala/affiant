namespace Affiant.Abstractions.Interfaces;

using Microsoft.SemanticKernel;

public interface IChatSessionStore
{
    Task<ChatSession> CreateAsync(string tenantId, string userId, CancellationToken ct);
    Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct);
    Task SaveMessagesAsync(string sessionId, IReadOnlyList<ChatMessageContent> messages, CancellationToken ct);
    Task<IReadOnlyList<ChatMessageContent>> LoadMessagesAsync(string sessionId, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);
}

public sealed record ChatSession(
    string SessionId,
    string TenantId,
    string UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);
