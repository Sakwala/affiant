using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;

namespace Affiant.EntityFramework.Stores;

public sealed class SqliteChatSessionStore(AffiantDbContext db) : IChatSessionStore
{
    public async Task<ChatSession> CreateAsync(string tenantId, string userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entity = new ChatSessionEntity
        {
            SessionId = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow
        };

        db.ChatSessions.Add(entity);
        await db.SaveChangesAsync(ct);

        return new ChatSession(entity.SessionId, entity.TenantId, entity.UserId, entity.CreatedAt, entity.LastActivityAt);
    }

    public async Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entity = await db.ChatSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

        return entity is null
            ? null
            : new ChatSession(entity.SessionId, entity.TenantId, entity.UserId, entity.CreatedAt, entity.LastActivityAt);
    }

    public async Task SaveMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var existing = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .ToListAsync(ct);
        db.ChatMessages.RemoveRange(existing);

        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
        if (session is not null)
            session.LastActivityAt = DateTimeOffset.UtcNow;

        for (var i = 0; i < messages.Count; i++)
        {
            db.ChatMessages.Add(ToEntity(messages[i], sessionId, ordinal: i));
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AppendMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (messages.Count == 0)
            return;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var maxOrdinal = await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .MaxAsync(m => (int?)m.Ordinal, ct) ?? -1;

        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
        if (session is not null)
            session.LastActivityAt = DateTimeOffset.UtcNow;

        for (var i = 0; i < messages.Count; i++)
        {
            db.ChatMessages.Add(ToEntity(messages[i], sessionId, ordinal: maxOrdinal + 1 + i));
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AffiantChatMessage>> LoadMessagesAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entities = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Ordinal)
            .ToListAsync(ct);

        return entities.Select(ToDomain).ToList();
    }

    public async Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
        if (session is not null)
        {
            db.ChatSessions.Remove(session);
            await db.SaveChangesAsync(ct);
        }
    }

    // ── Entity ↔ Domain mappers ──────────────────────────────────────────────

    private static ChatMessageEntity ToEntity(AffiantChatMessage message, string sessionId, int ordinal) =>
        new()
        {
            SessionId = sessionId,
            Ordinal = ordinal,
            Role = message.Role,
            Content = message.Content,
            AuthorName = message.AuthorName,
            ModelId = message.ModelId,
            ToolCallId = message.ToolCallId,
            FunctionName = message.FunctionName,
            ArgumentsJson = message.ArgumentsJson,
            Timestamp = DateTimeOffset.UtcNow
        };

    private static AffiantChatMessage ToDomain(ChatMessageEntity entity) =>
        new(entity.Role, entity.Content)
        {
            AuthorName = entity.AuthorName,
            ModelId = entity.ModelId,
            ToolCallId = entity.ToolCallId,
            FunctionName = entity.FunctionName,
            ArgumentsJson = entity.ArgumentsJson,
        };
}
