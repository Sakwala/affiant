using System.Collections.Concurrent;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

namespace Affiant.EntityFramework.Stores;

/// <summary>
/// Dependency-free <see cref="IChatSessionStore"/> for dev/test hosts that want no database at all.
/// Lives alongside <see cref="SqliteChatSessionStore"/> and <see cref="PostgresChatSessionStore"/> —
/// the package that already owns every <see cref="IChatSessionStore"/> implementation — rather than
/// in a new package, mirroring <c>InMemoryDocketStore</c>'s placement in <c>Affiant.Docket</c>
/// alongside its own SQL siblings despite carrying no EF dependency either.
/// </summary>
public sealed class InMemoryChatSessionStore : IChatSessionStore
{
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    private readonly ConcurrentDictionary<string, List<AffiantChatMessage>> _messages = new();
    private readonly object _messagesLock = new();

    public Task<ChatSession> CreateAsync(string tenantId, string userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var session = new ChatSession(Guid.NewGuid().ToString("N"), tenantId, userId, now, now);
        _sessions[session.SessionId] = session;
        return Task.FromResult(session);
    }

    public Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : null);
    }

    public Task SaveMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_messagesLock)
        {
            _messages[sessionId] = [.. messages];
        }
        TouchLastActivity(sessionId);
        return Task.CompletedTask;
    }

    public Task AppendMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (messages.Count == 0)
            return Task.CompletedTask;

        lock (_messagesLock)
        {
            var existing = _messages.GetOrAdd(sessionId, static _ => []);
            existing.AddRange(messages);
        }
        TouchLastActivity(sessionId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AffiantChatMessage>> LoadMessagesAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_messagesLock)
        {
            IReadOnlyList<AffiantChatMessage> loaded = _messages.TryGetValue(sessionId, out var list)
                ? [.. list]
                : Array.Empty<AffiantChatMessage>();
            return Task.FromResult(loaded);
        }
    }

    public Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _sessions.TryRemove(sessionId, out _);
        lock (_messagesLock)
        {
            _messages.TryRemove(sessionId, out _);
        }
        return Task.CompletedTask;
    }

    private void TouchLastActivity(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            _sessions[sessionId] = session with { LastActivityAt = DateTimeOffset.UtcNow };
    }
}
