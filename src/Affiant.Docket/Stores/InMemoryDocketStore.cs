using System.Collections.Concurrent;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using AbstractConversationContext = Affiant.Abstractions.Interfaces.ConversationContext;

namespace Affiant.Docket.Stores;

public sealed class InMemoryDocketStore : IDocketStore
{
    private readonly ConcurrentDictionary<string, AbstractConversationContext> _contexts = new();
    private readonly ConcurrentDictionary<Guid, DocketEntry> _entries = new();
    private readonly object _statusLock = new();

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
        return Task.FromResult(_entries.TryGetValue(entryId, out var entry) ? entry : null);
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
        var pending = _entries.Values
            .Where(e => e.SessionId == sessionId && e.Status == ReviewStatus.Pending)
            .ToList();
        return Task.FromResult<IReadOnlyList<DocketEntry>>(pending);
    }

    public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var expired = _entries.Values
            .Where(e => e.Status == ReviewStatus.Pending && e.ExpiresAt <= expiresBeforeUtc)
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
