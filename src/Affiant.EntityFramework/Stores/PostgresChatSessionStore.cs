using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Affiant.EntityFramework.Stores;

public sealed class PostgresChatSessionStore(
    AffiantDbContext db,
    ILogger<PostgresChatSessionStore> logger) : IChatSessionStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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

        return ToDomainRecord(entity);
    }

    public async Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entity = await db.ChatSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

        return entity is null ? null : ToDomainRecord(entity);
    }

    public async Task SaveMessagesAsync(string sessionId, IReadOnlyList<ChatMessageContent> messages, CancellationToken ct)
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
            var entity = ToEntity(messages[i], sessionId, ordinal: i);
            db.ChatMessages.Add(entity);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ChatMessageContent>> LoadMessagesAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entities = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Ordinal)
            .ToListAsync(ct);

        return entities.Select(ToDomainContent).ToList();
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

    private static ChatSession ToDomainRecord(ChatSessionEntity entity) =>
        new(entity.SessionId, entity.TenantId, entity.UserId, entity.CreatedAt, entity.LastActivityAt);

    private ChatMessageEntity ToEntity(ChatMessageContent message, string sessionId, int ordinal)
    {
        var entity = new ChatMessageEntity
        {
            SessionId = sessionId,
            Ordinal = ordinal,
            Role = message.Role.Label,
            Content = message.Content ?? string.Empty,
            AuthorName = message.AuthorName,
            ModelId = message.ModelId,
            Timestamp = DateTimeOffset.UtcNow
        };

        var funcCalls = message.Items.OfType<FunctionCallContent>().ToList();
        if (funcCalls.Count > 1)
        {
            logger.LogWarning(
                "ChatMessageContent has {Count} parallel FunctionCallContent items — only the first is stored; others will be lost on rehydration",
                funcCalls.Count);
        }

        if (funcCalls.Count >= 1)
        {
            var first = funcCalls[0];
            entity.ToolCallId = first.Id;
            entity.FunctionName = first.FunctionName;
            entity.ArgumentsJson = SerializeArguments(first.Arguments);
        }

        var funcResults = message.Items.OfType<FunctionResultContent>().ToList();
        if (funcResults.Count >= 1 && entity.ToolCallId is null)
        {
            var first = funcResults[0];
            entity.ToolCallId = first.CallId;
            entity.FunctionName = first.FunctionName;
            entity.Content = first.Result?.ToString() ?? string.Empty;
        }

        entity.MetadataJson = SerializeMetadata(message.Metadata);

        return entity;
    }

    private static ChatMessageContent ToDomainContent(ChatMessageEntity entity)
    {
        var role = new AuthorRole(entity.Role);
        var content = new ChatMessageContent(role, entity.Content)
        {
            AuthorName = entity.AuthorName,
            ModelId = entity.ModelId
        };

        if (entity.ToolCallId is not null && entity.FunctionName is not null)
        {
            if (role == AuthorRole.Assistant)
            {
                var args = DeserializeArguments(entity.ArgumentsJson);
                content.Items.Add(new FunctionCallContent(
                    functionName: entity.FunctionName,
                    pluginName: null,
                    id: entity.ToolCallId,
                    arguments: args));
            }
            else if (role == AuthorRole.Tool)
            {
                content.Items.Add(new FunctionResultContent(
                    functionName: entity.FunctionName,
                    pluginName: null,
                    callId: entity.ToolCallId,
                    result: entity.Content));
            }
        }

        return content;
    }

    // ── Serialization helpers ────────────────────────────────────────────────

    private static string? SerializeArguments(KernelArguments? args)
    {
        if (args is null) return null;
        var dict = args.ToDictionary(kv => kv.Key, kv => kv.Value);
        return JsonSerializer.Serialize(dict, s_jsonOptions);
    }

    private static KernelArguments? DeserializeArguments(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, s_jsonOptions);
        if (dict is null) return null;
        var args = new KernelArguments();
        foreach (var (k, v) in dict)
            args[k] = v;
        return args;
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;
        return JsonSerializer.Serialize(metadata, s_jsonOptions);
    }
}
