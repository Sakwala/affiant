using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AbstractConversationContext = Affiant.Abstractions.Models.ConversationContext;

namespace Affiant.EntityFramework.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IDocketStore"/>.
/// </summary>
/// <param name="db">The Affiant EF Core context.</param>
/// <param name="logger">Logger for the filing-race diagnostics.</param>
/// <param name="timeProvider">
/// The clock this store compares <see cref="DocketEntry.ExpiresAt"/> against when it projects
/// expiry onto a read (see <see cref="IDocketStore.GetDocketEntryAsync"/>) and when it stamps a
/// conversation context's last-updated instant. Defaults to <see cref="TimeProvider.System"/>; DI
/// supplies whatever the host registered, and a test substitutes a fake.
/// </param>
public sealed class PostgresDocketStore(
    AffiantDbContext db,
    ILogger<PostgresDocketStore> logger,
    TimeProvider? timeProvider = null) : IDocketStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private DocketEntry Project(DocketEntry entry) =>
        entry.Status == ReviewStatus.Pending && entry.ExpiresAt <= _time.GetUtcNow()
            ? entry with { Status = ReviewStatus.Expired }
            : entry;

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
            existing.LastUpdatedAt = _time.GetUtcNow();
        }
        else
        {
            db.ConversationContexts.Add(new ConversationContextEntity
            {
                SessionId = sessionId,
                EntitiesJson = entitiesJson,
                FieldValuesJson = "{}",
                ProvenanceChainsJson = "{}",
                LastUpdatedAt = _time.GetUtcNow()
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

        var entity = ToDocketEntity(entry);
        db.Docket.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The AnyAsync check above narrows but does not close the TOCTOU window — a
            // concurrent caller with the same EntryId (the client-supplied id used for
            // idempotent-retry filing) can win the race between the check and this insert.
            // EntryId is the primary key, so that race surfaces as a unique-constraint
            // violation here. Detach the failed insert so this DbContext doesn't keep retrying
            // it on a later SaveChangesAsync, then confirm the row landed before degrading to
            // the documented idempotent no-op — a genuine failure (not a race) must still throw.
            db.Entry(entity).State = EntityState.Detached;

            var wonByConcurrentCaller = await db.Docket.AsNoTracking()
                .AnyAsync(d => d.EntryId == entry.EntryId, ct);
            if (!wonByConcurrentCaller) throw;

            logger.LogDebug(
                "DocketEntry {EntryId} lost the filing race to a concurrent caller — degrading to idempotent no-op",
                entry.EntryId);
        }
    }

    public async Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entity = await db.Docket
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.EntryId == entryId, ct);

        return entity is null ? null : Project(ToDomainEntry(entity));
    }

    public async Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return await db.Docket
            .Where(d => d.EntryId == entryId && d.Status == ReviewStatus.Pending.ToString())
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status.ToString()), ct);
    }

    public async Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return await db.Docket
            .Where(d => d.EntryId == entryId
                && d.Status == ReviewStatus.Expired.ToString()
                && d.ResubmittedTo == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ResubmittedTo, newEntryId), ct);
    }

    public async Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entity = await db.Docket
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.ResubmittedTo == entryId, ct);

        return entity is null ? null : Project(ToDomainEntry(entity));
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

        var now = _time.GetUtcNow();
        var entities = await db.Docket
            .AsNoTracking()
            .Where(d => d.SessionId == sessionId
                && d.Status == ReviewStatus.Pending.ToString()
                && d.ExpiresAt > now)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(ToDomainEntry).ToList();
    }

    public async Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = _time.GetUtcNow();
        var entities = await db.Docket
            .AsNoTracking()
            .Where(d => d.Status == ReviewStatus.Pending.ToString() && d.ExpiresAt > now)
            .ToListAsync(ct);

        return entities.Select(ToDomainEntry).ToList();
    }

    public async Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(
        DateTimeOffset expiresBeforeUtc, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // Deadline order plus the limit is the page: the backlog drains oldest-first across ticks
        // and the whole Docket never loads into memory.
        var entities = await db.Docket
            .AsNoTracking()
            .Where(d => d.Status == ReviewStatus.Pending.ToString() && d.ExpiresAt <= expiresBeforeUtc)
            .OrderBy(d => d.ExpiresAt)
            .ThenBy(d => d.EntryId)
            .Take(limit)
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
        Status = entry.Status.ToString(),
        ResubmittedTo = entry.ResubmittedTo
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
            Amendments: DeserializeAmendments(entity.AmendmentsJson),
            ResubmittedTo: entity.ResubmittedTo);
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
