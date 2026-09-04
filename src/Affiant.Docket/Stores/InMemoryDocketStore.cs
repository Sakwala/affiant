using System.Collections.Concurrent;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using AbstractConversationContext = Affiant.Abstractions.Models.ConversationContext;

namespace Affiant.Docket.Stores;

/// <summary>
/// Process-local <see cref="IDocketStore"/>. Nothing survives a restart — see
/// <see cref="Affiant.Docket.Options.DocketOptions"/> for the durable backends.
/// </summary>
/// <param name="timeProvider">
/// The clock this store compares <see cref="DocketEntry.ExpiresAt"/> against when it projects
/// expiry onto a read (see <see cref="IDocketStore.GetDocketEntryAsync"/>). Defaults to
/// <see cref="TimeProvider.System"/>; DI supplies whatever the host registered, and a test
/// substitutes a fake.
/// </param>
public sealed class InMemoryDocketStore(TimeProvider? timeProvider = null) : IDocketStore
{
    private readonly ConcurrentDictionary<string, AbstractConversationContext> _contexts = new();
    private readonly ConcurrentDictionary<Guid, DocketEntry> _entries = new();
    private readonly object _statusLock = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private DocketEntry Project(DocketEntry entry) =>
        entry.Status == ReviewStatus.Pending && entry.ExpiresAt <= _time.GetUtcNow()
            ? entry with { Status = ReviewStatus.Expired }
            : entry;

    public Task SaveContextAsync(string sessionId, AbstractConversationContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _contexts[sessionId] = context;
        return Task.CompletedTask;
    }

    public Task<AbstractConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_contexts.TryGetValue(sessionId, out var ctx) ? ctx : null);
    }

    public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Same lock as the status-transition guard — first write wins, matching the
        // documented idempotency contract (a second call for an existing EntryId is a no-op,
        // not an overwrite, even onto an already-terminal entry).
        lock (_statusLock)
        {
            _entries.TryAdd(entry.EntryId, entry);
        }
        return Task.CompletedTask;
    }

    public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_entries.TryGetValue(entryId, out var entry) ? Project(entry) : null);
    }

    public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Lock ensures the check-and-update is atomic — same double-submit guard as the DB stores.
        lock (_statusLock)
        {
            if (!_entries.TryGetValue(entryId, out var existing) || existing.Status != ReviewStatus.Pending)
                return Task.FromResult(0);

            _entries[entryId] = existing with { Status = status };
            return Task.FromResult(1);
        }
    }

    public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_statusLock)
        {
            if (!_entries.TryGetValue(entryId, out var existing)
                || existing.Status != ReviewStatus.Expired
                || existing.ResubmittedTo is not null)
            {
                return Task.FromResult(0);
            }

            _entries[entryId] = existing with { ResubmittedTo = newEntryId };
            return Task.FromResult(1);
        }
    }

    public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parent = _entries.Values.FirstOrDefault(e => e.ResubmittedTo == entryId);
        return Task.FromResult(parent is null ? null : Project(parent));
    }

    public Task UpdateAmendmentsAsync(
        Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_statusLock)
        {
            if (_entries.TryGetValue(entryId, out var existing))
                _entries[entryId] = existing with { Amendments = amendments };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();
        var pending = _entries.Values
            .Where(e => e.SessionId == sessionId && e.Status == ReviewStatus.Pending && e.ExpiresAt > now)
            .OrderBy(e => e.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<DocketEntry>>(pending);
    }

    public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();
        var pending = _entries.Values
            .Where(e => e.Status == ReviewStatus.Pending && e.ExpiresAt > now)
            .ToList();
        return Task.FromResult<IReadOnlyList<DocketEntry>>(pending);
    }

    public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(
        DateTimeOffset expiresBeforeUtc, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // Persisted status, not the read-time expiry projection: this is the query that finds rows
        // whose Expired transition has not been committed yet. Ordered by deadline so a backlog
        // larger than one page drains oldest-first across ticks instead of starving.
        var expired = _entries.Values
            .Where(e => e.Status == ReviewStatus.Pending && e.ExpiresAt <= expiresBeforeUtc)
            .OrderBy(e => e.ExpiresAt)
            .ThenBy(e => e.EntryId)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<DocketEntry>>(expired);
    }

    public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var ids = entryIds.ToHashSet();
        lock (_statusLock)
        {
            foreach (var id in ids)
            {
                if (_entries.TryGetValue(id, out var entry) && entry.Status == ReviewStatus.Pending)
                    _entries[id] = entry with { Status = ReviewStatus.Expired };
            }
        }
        return Task.CompletedTask;
    }
}
