namespace Affiant.Transport.SignalR.Tests.Infrastructure;

using Affiant.Abstractions.Interfaces;
using Microsoft.SemanticKernel;

/// <summary>
/// No-op IChatSessionStore for wiring AffiantHub in integration tests.
/// </summary>
internal sealed class NullChatSessionStore : IChatSessionStore
{
    public Task<ChatSession> CreateAsync(string tenantId, string userId, CancellationToken ct)
        => Task.FromResult(new ChatSession(
            Guid.NewGuid().ToString(), tenantId, userId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    public Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct)
        => Task.FromResult<ChatSession?>(null);

    public Task SaveMessagesAsync(
        string sessionId, IReadOnlyList<ChatMessageContent> messages, CancellationToken ct)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ChatMessageContent>> LoadMessagesAsync(
        string sessionId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ChatMessageContent>>(Array.Empty<ChatMessageContent>());

    public Task DeleteAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
}
