namespace Affiant.Transport.SignalR.Tests.Infrastructure;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

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
        string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct)
        => Task.CompletedTask;

    public Task AppendMessagesAsync(
        string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct)
        => Task.CompletedTask;

    public Task<IReadOnlyList<AffiantChatMessage>> LoadMessagesAsync(
        string sessionId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AffiantChatMessage>>(Array.Empty<AffiantChatMessage>());

    public Task DeleteAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
}
