using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.EntityFramework;
using Affiant.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AbstractConversationContext = Affiant.Abstractions.Interfaces.ConversationContext;

namespace Affiant.Docket.Stores;

public sealed class PostgresDocketStore(
    AffiantDbContext db,
    ILogger<PostgresDocketStore> logger) : IDocketStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task SaveContextAsync(string sessionId, AbstractConversationContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entitiesJson = JsonSerializer.Serialize(context.Entities, s_jsonOptions);

        var existing = await db.ConversationContexts
            .FirstOrDefaultAsync(c => c.SessionId == sessionId, ct);

        if (existing is not null)
        {
            existing.EntitiesJson = entitiesJson;
            existing.FieldValuesJson = "{}";
            existing.ProvenanceChainsJson = "{}";
            existing.LastUpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            db.ConversationContexts.Add(new ConversationContextEntity
            {
                SessionId = sessionId,
                EntitiesJson = entitiesJson,
                FieldValuesJson = "{}",
                ProvenanceChainsJson = "{}",
                LastUpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<AbstractConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entity = await db.ConversationContexts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.SessionId == sessionId, ct);

        if (entity is null) return null;

        var entities = JsonSerializer.Deserialize<Dictionary<string, EntityRef>>(
            entity.EntitiesJson, s_jsonOptions) ?? new Dictionary<string, EntityRef>();

        return new AbstractConversationContext(sessionId, entities);
    }

    public async Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var exists = await db.Docket.AnyAsync(d => d.EntryId == entry.EntryId, ct);
        if (exists)
        {
            logger.LogDebug("DocketEntry {EntryId} already exists — skipping duplicate insert", entry.EntryId);
            return;
        }

        db.Docket.Add(ToDocketEntity(entry));
        await db.SaveChangesAsync(ct);
    }

    public async Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entity = await db.Docket
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.EntryId == entryId, ct);

        return entity is null ? null : ToDomainEntry(entity);
    }

    public async Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return await db.Docket
            .Where(d => d.EntryId == entryId && d.Status == ReviewStatus.Pending.ToString())
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status.ToString()), ct);
    }

    public async Task UpdateAmendmentsAsync(
        Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(amendments, s_jsonOptions);
        await db.Docket
            .Where(d => d.EntryId == entryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AmendmentsJson, json), ct);
    }

    public async Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entities = await db.Docket
            .AsNoTracking()
            .Where(d => d.SessionId == sessionId && d.Status == ReviewStatus.Pending.ToString())
            .ToListAsync(ct);

        return entities.Select(ToDomainEntry).ToList();
    }

    public async Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entities = await db.Docket
            .AsNoTracking()
            .Where(d => d.Status == ReviewStatus.Pending.ToString() && d.ExpiresAt <= expiresBeforeUtc)
            .ToListAsync(ct);

        return entities.Select(ToDomainEntry).ToList();
    }

    public async Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var ids = entryIds.ToList();
        if (ids.Count == 0) return;

        var pending = ReviewStatus.Pending.ToString();
        var expired = ReviewStatus.Expired.ToString();

        await db.Docket
            .Where(d => ids.Contains(d.EntryId) && d.Status == pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, expired), ct);
    }

    // ── DocketEntry ↔ DocketEntryEntity ─────────────────────────────────────

    private static DocketEntryEntity ToDocketEntity(DocketEntry entry) => new()
    {
        EntryId = entry.EntryId,
        SessionId = entry.SessionId,
        TenantId = entry.TenantId,
        UserId = entry.UserId,
        ReviewerUserId = entry.ReviewerUserId,
        OperationType = entry.OperationType,
        AffidavitJson = JsonSerializer.Serialize(entry.Envelope, s_jsonOptions),
        ProvenanceChainsJson = SerializeProvenanceChains(entry.Envelope.Fields),
        AmendmentsJson = entry.Amendments is not null
            ? JsonSerializer.Serialize(entry.Amendments, s_jsonOptions)
            : null,
        CreatedAt = entry.CreatedAt,
        ExpiresAt = entry.ExpiresAt,
        Status = entry.Status.ToString()
    };

    private static DocketEntry ToDomainEntry(DocketEntryEntity entity)
    {
        var affidavit = JsonSerializer.Deserialize<Affidavit>(entity.AffidavitJson, s_jsonOptions)!;

        var chains = DeserializeProvenanceChains(entity.ProvenanceChainsJson);
        var fieldsWithProvenance = affidavit.Fields
            .Select(f => chains.TryGetValue(f.Name, out var chain) ? f with { Provenance = chain } : f)
            .ToArray();
        affidavit = affidavit with { Fields = fieldsWithProvenance };

        return new DocketEntry(
            EntryId: entity.EntryId,
            SessionId: entity.SessionId,
            TenantId: entity.TenantId,
            UserId: entity.UserId,
            ReviewerUserId: entity.ReviewerUserId,
            OperationType: entity.OperationType,
            Envelope: affidavit,
            Status: Enum.Parse<ReviewStatus>(entity.Status),
            CreatedAt: entity.CreatedAt,
            ExpiresAt: entity.ExpiresAt,
            Amendments: DeserializeAmendments(entity.AmendmentsJson));
    }

    private static IReadOnlyDictionary<string, object?>? DeserializeAmendments(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, s_jsonOptions);
        if (raw is null) return null;

        var result = new Dictionary<string, object?>(raw.Count);
        foreach (var (k, v) in raw)
        {
            result[k] = v.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => v.GetString(),
                _ => v
            };
        }
        return result;
    }

    private static string SerializeProvenanceChains(AffidavitField[] fields)
    {
        var dict = fields.ToDictionary(f => f.Name, f => f.Provenance);
        return JsonSerializer.Serialize(dict, s_jsonOptions);
    }

    private static Dictionary<string, ProvenanceChain> DeserializeProvenanceChains(string json)
    {
        if (string.IsNullOrEmpty(json) || json is "[]" or "{}")
            return new Dictionary<string, ProvenanceChain>();

        return JsonSerializer.Deserialize<Dictionary<string, ProvenanceChain>>(json, s_jsonOptions)
               ?? new Dictionary<string, ProvenanceChain>();
    }
}
