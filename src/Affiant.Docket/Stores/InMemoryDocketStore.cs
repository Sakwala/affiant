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

    private DocketEntry Project(DocketEntry entry) => DocketRow.Project(entry, _time.GetUtcNow());

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

        // A row is filed pending and leaves that state only through the guarded transition (AZ-1).
        DocketRow.ValidateFiling(entry, nameof(entry));

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


    // ── The scoped, guarded, paged surface ──────────────────────────────────
    // This half of the store is the reference a SQL store earns its name by passing the same tests
    // as. Every guarded write here holds _statusLock for the whole read-and-write, which is the
    // in-memory equivalent of the conditional UPDATE the SQL stores issue: there is no interleaving
    // point between deciding that a row is still pending and writing that it is not.

    public Task<DocketTransitionResult> TransitionAsync(
        Guid entryId,
        DocketScope scope,
        ReviewStatus expected,
        DocketTransitionPatch patch,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DocketRow.RequireTenant(scope, nameof(scope));
        DocketRow.ValidateTransition(patch, expected, nameof(patch), nameof(expected));

        var now = _time.GetUtcNow();
        lock (_statusLock)
        {
            if (!_entries.TryGetValue(entryId, out var existing) || !DocketRow.InScope(existing, scope))
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.NotFound());

            // A blocked entry sits in pending and is never decided, never executed and never
            // degraded to a weaker requirement. The gate refuses such a decision earlier, with the
            // blocked code in the details; this is the same refusal one layer down, so a host that
            // reaches the store directly cannot get past it either. Expiry is exempt: a blocked
            // entry still runs out of time like any other.
            if (existing.Blocked is not null && patch.Status != ReviewStatus.Expired)
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.AlreadyDecided());

            var read = DocketRow.ReadStatus(existing, now);
            if (read == ReviewStatus.Expired && patch.Status != ReviewStatus.Expired)
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.Expired());
            if (existing.Status != ReviewStatus.Pending)
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.AlreadyDecided());

            var updated = DocketRow.Apply(existing, patch, now);
            _entries[entryId] = updated;
            return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.Transitioned(updated));
        }
    }

    public Task<PreserveAmendmentsResult> PreserveAmendmentsAsync(
        Guid entryId,
        DocketScope scope,
        IReadOnlyDictionary<string, object?> amendments,
        PreservedAct act,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DocketRow.RequireTenant(scope, nameof(scope));
        ArgumentNullException.ThrowIfNull(amendments);
        ArgumentNullException.ThrowIfNull(act);

        var now = _time.GetUtcNow();
        lock (_statusLock)
        {
            if (!_entries.TryGetValue(entryId, out var existing) || !DocketRow.InScope(existing, scope))
                return Task.FromResult<PreserveAmendmentsResult>(new PreserveAmendmentsResult.NotFound());

            if (DocketRow.ReadStatus(existing, now) != ReviewStatus.Expired)
                return Task.FromResult<PreserveAmendmentsResult>(new PreserveAmendmentsResult.NotExpired());

            // Touches PreservedAmendments and nothing else — never the status, the decision or the
            // attestation. What an approval ACCEPTED lives in Amendments; nobody accepted these.
            var updated = existing with
            {
                PreservedAmendments = new PreservedAmendments(amendments, act.At, act.By)
            };
            _entries[entryId] = updated;
            return Task.FromResult<PreserveAmendmentsResult>(
                new PreserveAmendmentsResult.Preserved(DocketRow.Project(updated, now)));
        }
    }

    public Task<RecordExecutionResult> RecordExecutionAsync(
        Guid entryId,
        DocketScope scope,
        ExecutionOutcome outcome,
        string? detail,
        ExecutionOutcome expected,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DocketRow.RequireTenant(scope, nameof(scope));
        DocketRow.ValidateExecutionReport(outcome, expected, nameof(outcome), nameof(expected));

        lock (_statusLock)
        {
            if (!_entries.TryGetValue(entryId, out var existing) || !DocketRow.InScope(existing, scope))
                return Task.FromResult<RecordExecutionResult>(new RecordExecutionResult.NotFound());

            // AZ-5: an executor is reachable only through a row that carries an attestation, and an
            // approved row with nobody on it is not an authorised write either.
            if (existing.Status != ReviewStatus.Approved || !DocketRow.MayRecordExecution(existing))
                return Task.FromResult<RecordExecutionResult>(new RecordExecutionResult.NotApproved());

            if ((existing.Execution ?? ExecutionOutcome.Unexecuted) != ExecutionOutcome.Unexecuted)
            {
                return Task.FromResult<RecordExecutionResult>(
                    new RecordExecutionResult.ExecutionAlreadyRecorded());
            }

            var updated = existing with { Execution = outcome, ExecutionDetail = detail };
            _entries[entryId] = updated;
            return Task.FromResult<RecordExecutionResult>(new RecordExecutionResult.Recorded(updated));
        }
    }

    public Task<RecordSupersessionResult> RecordSupersessionAsync(
        Guid entryId, DocketScope scope, Guid supersededBy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DocketRow.RequireTenant(scope, nameof(scope));

        var now = _time.GetUtcNow();
        lock (_statusLock)
        {
            if (!_entries.TryGetValue(entryId, out var existing) || !DocketRow.InScope(existing, scope))
                return Task.FromResult<RecordSupersessionResult>(new RecordSupersessionResult.NotFound());

            if (DocketRow.ReadStatus(existing, now) == ReviewStatus.Pending
                || existing.ResubmittedTo is not null)
            {
                return Task.FromResult<RecordSupersessionResult>(new RecordSupersessionResult.NotTerminal());
            }

            var updated = existing with { ResubmittedTo = supersededBy };
            _entries[entryId] = updated;
            return Task.FromResult<RecordSupersessionResult>(
                new RecordSupersessionResult.Recorded(DocketRow.Project(updated, now)));
        }
    }

    public Task<int> MarkBlockedAsync(
        Guid entryId, DocketScope scope, BlockedMarker marker, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(marker);
        DocketRow.RequireTenant(scope, nameof(scope));

        lock (_statusLock)
        {
            if (!_entries.TryGetValue(entryId, out var existing)
                || !DocketRow.InScope(existing, scope)
                || existing.Status != ReviewStatus.Pending
                || existing.Blocked is not null)
            {
                return Task.FromResult(0);
            }

            _entries[entryId] = existing with { Blocked = marker };
            return Task.FromResult(1);
        }
    }

    public Task<long> CountPendingAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();
        return Task.FromResult(
            _entries.Values.LongCount(e => DocketRow.ReadStatus(e, now) == ReviewStatus.Pending));
    }

    public Task<DocketPageResult<DocketEntry>> ListPendingAsync(
        DocketScope scope, DocketPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();
        return Task.FromResult(Paginate(
            DocketCursor.PendingListing,
            _entries.Values.Where(e =>
                DocketRow.InScope(e, scope) && DocketRow.ReadStatus(e, now) == ReviewStatus.Pending),
            page));
    }

    public Task<DocketPageResult<DocketEntry>> ListApprovedUnexecutedAsync(
        DocketScope scope, DocketPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Paginate(
            DocketCursor.ApprovedUnexecutedListing,
            _entries.Values.Where(e => DocketRow.InScope(e, scope) && DocketRow.IsApprovedUnexecuted(e)),
            page));
    }

    public Task<ExpireDueResult> ExpireDueAsync(
        DateTimeOffset now, DocketScope scope, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        lock (_statusLock)
        {
            // limit + 1 candidates, so `more` is answered by this same read rather than by a second
            // query a concurrent write could change under.
            var due = _entries.Values
                .Where(e => DocketRow.InScope(e, scope)
                    && e.Status == ReviewStatus.Pending
                    && e.ExpiresAt <= now)
                .OrderBy(e => e.ExpiresAt)
                .ThenBy(e => e.EntryId)
                .Take(limit + 1)
                .ToList();

            var more = due.Count > limit;
            var expired = new List<DocketEntry>(Math.Min(due.Count, limit));
            foreach (var entry in due.Take(limit))
            {
                // Re-test under the same lock: a decision that landed between the read above and
                // here owns that row, and this sweep must not report it as its own.
                if (!_entries.TryGetValue(entry.EntryId, out var current)
                    || current.Status != ReviewStatus.Pending)
                {
                    continue;
                }

                var updated = current with
                {
                    Status = ReviewStatus.Expired,
                    DecidedAt = current.DecidedAt ?? current.ExpiresAt
                };
                _entries[entry.EntryId] = updated;
                expired.Add(updated);
            }

            return Task.FromResult(new ExpireDueResult(expired, more));
        }
    }

    public Task<RetentionResult> ApplyRetentionAsync(
        DocketRetentionPolicy policy, DocketScope scope, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var now = _time.GetUtcNow();
        lock (_statusLock)
        {
            var eligible = _entries.Values
                .Where(e => DocketRow.InScope(e, scope)
                    && !DocketRow.IsApprovedUnexecuted(e)
                    && DocketRow.TerminalAt(e, now) is { } terminalAt
                    && terminalAt < policy.OlderThan)
                .OrderBy(e => e.CreatedAt)
                .ThenBy(e => e.EntryId)
                .Take(limit + 1)
                .ToList();

            var more = eligible.Count > limit;
            var removed = 0;
            foreach (var entry in eligible.Take(limit))
            {
                if (_entries.TryRemove(entry.EntryId, out _)) removed++;
            }

            return Task.FromResult(new RetentionResult(removed, more));
        }
    }

    public Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        lock (_statusLock)
        {
            var doomed = _entries.Values.Where(e => e.TenantId == tenantId).Select(e => e.EntryId).ToList();
            var removed = doomed.Count(id => _entries.TryRemove(id, out _));
            return Task.FromResult(removed);
        }
    }

    public async IAsyncEnumerable<DocketEntry> ExportAsync(
        DocketScope scope,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var now = _time.GetUtcNow();

        // A snapshot of the ids, then a lookup per id: the export never holds the whole Docket in
        // one list, and a row removed mid-export is simply not yielded rather than throwing.
        var ordered = _entries.Values
            .Where(e => DocketRow.InScope(e, scope))
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.EntryId)
            .Select(e => e.EntryId)
            .ToList();

        foreach (var id in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (_entries.TryGetValue(id, out var entry))
                yield return DocketRow.Project(entry, now);
            await Task.Yield();
        }
    }

    /// <summary>
    /// One page of <paramref name="source"/> in filing order, resuming after
    /// <paramref name="page"/>'s cursor.
    /// </summary>
    /// <remarks>
    /// <b>A page boundary never falls inside a filing instant</b> — the same rule the SQL stores
    /// page by, implemented the same way here so the three backends hand out the same pages for the
    /// same Docket. See <c>EfDocketOperations.PageAsync</c> for why the cursor carries only the
    /// instant.
    /// </remarks>
    private DocketPageResult<DocketEntry> Paginate(
        string listing, IEnumerable<DocketEntry> source, DocketPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfLessThan(page.Limit, 1);

        var now = _time.GetUtcNow();
        var candidates = source;
        if (DocketCursor.TryDecode(page.Cursor, listing, out var afterAt, out _))
            candidates = candidates.Where(e => e.CreatedAt > afterAt);

        var ordered = candidates.OrderBy(e => e.CreatedAt).ThenBy(e => e.EntryId).ToList();
        var window = ordered.Take(page.Limit + 1).ToList();

        var more = window.Count > page.Limit;
        var taken = window.Take(page.Limit).ToList();

        if (more && taken.Count > 0 && window[page.Limit].CreatedAt == taken[^1].CreatedAt)
        {
            var boundary = taken[^1].CreatedAt;
            var trimmed = taken.Where(e => e.CreatedAt != boundary).ToList();
            if (trimmed.Count > 0)
            {
                taken = trimmed;
            }
            else
            {
                taken = ordered.Where(e => e.CreatedAt == boundary).ToList();
                more = ordered.Any(e => e.CreatedAt > boundary);
            }
        }

        var items = taken.Select(e => DocketRow.Project(e, now)).ToList();
        var cursor = more && items.Count > 0
            ? DocketCursor.Encode(listing, items[^1].CreatedAt, items[^1].EntryId)
            : null;

        return new DocketPageResult<DocketEntry>(items, cursor, more);
    }
}
