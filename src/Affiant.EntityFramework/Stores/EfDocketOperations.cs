using Affiant.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AbstractConversationContext = Affiant.Abstractions.Models.ConversationContext;

namespace Affiant.EntityFramework.Stores;

/// <summary>
/// The whole <see cref="IDocketStore"/> contract over EF Core, written once and shared by both SQL
/// stores.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SqliteDocketStore"/> and <see cref="PostgresDocketStore"/> used to carry two near-copies
/// of this code that had already begun to diverge: SQLite could not translate a
/// <c>DateTimeOffset</c> comparison into SQL, so its listings loaded every candidate row and filtered
/// in memory while Postgres filtered in the database. The sortable tick columns
/// (<see cref="DocketEntryEntity.CreatedAtTicks"/> and friends) removed the reason for the divergence,
/// and this type removes the divergence: both backends now run the same query shapes and are held to
/// the same fixtures, which is what makes either of them a reference for the other.
/// </para>
/// <para>
/// Every guarded write here is a real conditional <c>UPDATE</c> — the guard is in the statement, never
/// in surrounding C# — so of two decisions that race, exactly one affects a row.
/// </para>
/// </remarks>
internal sealed class EfDocketOperations(AffiantDbContext db, ILogger logger, TimeProvider time)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string s_pending = ReviewStatus.Pending.ToString();
    private static readonly string s_approved = ReviewStatus.Approved.ToString();
    private static readonly string s_expired = ReviewStatus.Expired.ToString();
    private static readonly string s_unexecuted = ExecutionOutcome.Unexecuted.ToString();

    // ── Conversation context ────────────────────────────────────────────────

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
            existing.LastUpdatedAt = time.GetUtcNow();
        }
        else
        {
            db.ConversationContexts.Add(new ConversationContextEntity
            {
                SessionId = sessionId,
                EntitiesJson = entitiesJson,
                FieldValuesJson = "{}",
                ProvenanceChainsJson = "{}",
                LastUpdatedAt = time.GetUtcNow()
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

    // ── Filing and reading ──────────────────────────────────────────────────

    public async Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
    {
        // A row is filed pending and leaves that state only through the guarded transition (AZ-1).
        DocketRow.ValidateFiling(entry, nameof(entry));

        ct.ThrowIfCancellationRequested();

        var exists = await db.Docket.AnyAsync(d => d.EntryId == entry.EntryId, ct);
        if (exists)
        {
            logger.LogDebug("DocketEntry {EntryId} already exists — skipping duplicate insert", entry.EntryId);
            return;
        }

        var entity = ToEntity(entry);
        db.Docket.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The AnyAsync check above narrows but does not close the TOCTOU window — a concurrent
            // caller with the same EntryId (the client-supplied id used for idempotent-retry filing)
            // can win the race between the check and this insert. EntryId is the primary key, so
            // that race surfaces as a unique-constraint violation here. Detach the failed insert so
            // this DbContext doesn't keep retrying it on a later SaveChangesAsync, then confirm the
            // row landed before degrading to the documented idempotent no-op — a genuine failure
            // (not a race) must still throw.
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

        var entity = await db.Docket.AsNoTracking().FirstOrDefaultAsync(d => d.EntryId == entryId, ct);
        return entity is null ? null : DocketRow.Project(ToDomain(entity), time.GetUtcNow());
    }

    public async Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return await db.Docket
            .Where(d => d.EntryId == entryId && d.Status == s_expired && d.ResubmittedTo == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ResubmittedTo, newEntryId), ct);
    }

    public async Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entity = await db.Docket.AsNoTracking().FirstOrDefaultAsync(d => d.ResubmittedTo == entryId, ct);
        return entity is null ? null : DocketRow.Project(ToDomain(entity), time.GetUtcNow());
    }

    public async Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var nowTicks = time.GetUtcNow().UtcTicks;
        var entities = await db.Docket
            .AsNoTracking()
            .Where(d => d.SessionId == sessionId && d.Status == s_pending && d.ExpiresAtTicks > nowTicks)
            .OrderBy(d => d.CreatedAtTicks)
            .ThenBy(d => d.EntryId)
            .ToListAsync(ct);

        return entities.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var nowTicks = time.GetUtcNow().UtcTicks;
        var entities = await db.Docket
            .AsNoTracking()
            .Where(d => d.Status == s_pending && d.ExpiresAtTicks > nowTicks)
            .OrderBy(d => d.CreatedAtTicks)
            .ThenBy(d => d.EntryId)
            .ToListAsync(ct);

        return entities.Select(ToDomain).ToList();
    }

    // ── The guarded transitions ─────────────────────────────────────────────

    public async Task<DocketTransitionResult> TransitionAsync(
        Guid entryId,
        DocketScope scope,
        ReviewStatus expected,
        DocketTransitionPatch patch,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DocketRow.RequireTenant(scope, nameof(scope));
        DocketRow.ValidateTransition(patch, expected, nameof(patch), nameof(expected));

        var now = time.GetUtcNow();
        var nowTicks = now.UtcTicks;
        var decidedAt = patch.DecidedAt ?? now;
        var status = patch.Status.ToString();
        var execution = patch.Status == ReviewStatus.Approved
            ? (patch.Execution ?? ExecutionOutcome.Unexecuted).ToString()
            : null;
        var decisionJson = DocketRowSerialization.WriteDecision(patch.Decision);
        var attestationJson = DocketRowSerialization.WriteAttestation(patch.Attestation);
        var amendmentsJson = DocketRowSerialization.WriteAmendments(patch.Amendments);
        var amendedJson = patch.AmendedAffidavit is null
            ? null
            : JsonSerializer.Serialize(patch.AmendedAffidavit, s_jsonOptions);
        var amendedChainsJson = patch.AmendedAffidavit is null
            ? null
            : SerializeProvenanceChains(patch.AmendedAffidavit.Fields);

        var executionDetail = patch.ExecutionDetail;
        var supersededBy = patch.SupersededBy;

        var guard = Scoped(scope).Where(d => d.EntryId == entryId && d.Status == s_pending);

        // A blocked entry sits in pending and is never decided, never executed and never degraded to
        // a weaker requirement — but it still runs out of time like any other, so the sweep's own
        // transition to Expired is exempt from that half of the guard, and so is the deadline half.
        if (patch.Status != ReviewStatus.Expired)
            guard = guard.Where(d => d.BlockedJson == null && d.ExpiresAtTicks > nowTicks);

        var rows = await guard.ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, status)
            .SetProperty(d => d.Execution, execution)
            .SetProperty(d => d.DecidedAt, (DateTimeOffset?)decidedAt)
            .SetProperty(d => d.DecidedAtTicks, (long?)decidedAt.UtcTicks)
            .SetProperty(d => d.DecisionJson, d => decisionJson ?? d.DecisionJson)
            .SetProperty(d => d.AttestationJson, d => attestationJson ?? d.AttestationJson)
            .SetProperty(d => d.AmendmentsJson, d => amendmentsJson ?? d.AmendmentsJson)
            .SetProperty(d => d.AmendedAffidavitJson, d => amendedJson ?? d.AmendedAffidavitJson)
            .SetProperty(d => d.AmendedProvenanceChainsJson, d => amendedChainsJson ?? d.AmendedProvenanceChainsJson)
            .SetProperty(d => d.ExecutionDetail, d => executionDetail ?? d.ExecutionDetail)
            .SetProperty(d => d.ResubmittedTo, d => supersededBy ?? d.ResubmittedTo), ct);

        if (rows > 0)
        {
            var updated = await ReadScopedAsync(entryId, scope, ct);
            return updated is null
                ? new DocketTransitionResult.NotFound()
                : new DocketTransitionResult.Transitioned(updated);
        }

        // The write did not apply. Read the row to say WHY, because the three refusals mean
        // different things to the caller and map to different codes — and read it UNPROJECTED, since
        // telling "somebody else decided this" from "this ran out of time" is exactly the question
        // the deadline projection would answer for us, the same way, for both.
        var current = await ReadScopedRawAsync(entryId, scope, ct);
        if (current is null) return new DocketTransitionResult.NotFound();

        // A blocked row sits in pending and refuses every decision. It is not "expired" — nothing
        // ran out of time — so it takes the not-pending arm, and the gate that has the row in hand
        // reports the marker's own code beside it.
        if (current.Blocked is not null && patch.Status != ReviewStatus.Expired)
            return new DocketTransitionResult.AlreadyDecided();

        // Still pending after a guard that also tested the deadline means the deadline is what
        // refused it; already Expired means the same thing, recorded.
        return current.Status is ReviewStatus.Pending or ReviewStatus.Expired
            ? new DocketTransitionResult.Expired()
            : new DocketTransitionResult.AlreadyDecided();
    }

    public async Task<PreserveAmendmentsResult> PreserveAmendmentsAsync(
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

        var now = time.GetUtcNow();
        var nowTicks = now.UtcTicks;
        var json = DocketRowSerialization.WritePreservedAmendments(
            new PreservedAmendments(amendments, act.At, act.By));

        // Touches PreservedAmendments and nothing else — never the status, the decision or the
        // attestation. Guarded on the row READING expired, which is either a persisted Expired or a
        // pending row past its deadline the sweep has not reached.
        var rows = await Scoped(scope)
            .Where(d => d.EntryId == entryId
                && (d.Status == s_expired || (d.Status == s_pending && d.ExpiresAtTicks <= nowTicks)))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PreservedAmendmentsJson, json), ct);

        if (rows > 0)
        {
            var updated = await ReadScopedAsync(entryId, scope, ct);
            return updated is null
                ? new PreserveAmendmentsResult.NotFound()
                : new PreserveAmendmentsResult.Preserved(updated);
        }

        var current = await ReadScopedAsync(entryId, scope, ct);
        return current is null
            ? new PreserveAmendmentsResult.NotFound()
            : new PreserveAmendmentsResult.NotExpired();
    }

    public async Task<RecordExecutionResult> RecordExecutionAsync(
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

        var name = outcome.ToString();
        var rows = await Scoped(scope)
            // AZ-5: an approved row with no attestation is not an authorised write, and the store
            // will not record an outcome against one.
            .Where(d => d.EntryId == entryId
                && d.Status == s_approved
                && d.AttestationJson != null
                && (d.Execution == null || d.Execution == s_unexecuted))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Execution, name)
                .SetProperty(d => d.ExecutionDetail, detail), ct);

        if (rows > 0)
        {
            var updated = await ReadScopedAsync(entryId, scope, ct);
            return updated is null
                ? new RecordExecutionResult.NotFound()
                : new RecordExecutionResult.Recorded(updated);
        }

        var current = await ReadScopedAsync(entryId, scope, ct);
        if (current is null) return new RecordExecutionResult.NotFound();
        return current.Status == ReviewStatus.Approved && DocketRow.MayRecordExecution(current)
            ? new RecordExecutionResult.ExecutionAlreadyRecorded()
            : new RecordExecutionResult.NotApproved();
    }

    public async Task<RecordSupersessionResult> RecordSupersessionAsync(
        Guid entryId, DocketScope scope, Guid supersededBy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DocketRow.RequireTenant(scope, nameof(scope));

        var nowTicks = time.GetUtcNow().UtcTicks;
        var rows = await Scoped(scope)
            .Where(d => d.EntryId == entryId
                && d.ResubmittedTo == null
                && (d.Status != s_pending || d.ExpiresAtTicks <= nowTicks))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ResubmittedTo, supersededBy), ct);

        if (rows > 0)
        {
            var updated = await ReadScopedAsync(entryId, scope, ct);
            return updated is null
                ? new RecordSupersessionResult.NotFound()
                : new RecordSupersessionResult.Recorded(updated);
        }

        var current = await ReadScopedAsync(entryId, scope, ct);
        return current is null
            ? new RecordSupersessionResult.NotFound()
            : new RecordSupersessionResult.NotTerminal();
    }

    public async Task<int> MarkBlockedAsync(
        Guid entryId, DocketScope scope, BlockedMarker marker, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(marker);
        DocketRow.RequireTenant(scope, nameof(scope));

        var json = DocketRowSerialization.WriteBlocked(marker);
        return await Scoped(scope)
            .Where(d => d.EntryId == entryId && d.Status == s_pending && d.BlockedJson == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.BlockedJson, json), ct);
    }

    // ── The bounded reads ───────────────────────────────────────────────────

    public async Task<DocketPageResult<DocketEntry>> ListPendingAsync(
        DocketScope scope, DocketPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfLessThan(page.Limit, 1);

        var nowTicks = time.GetUtcNow().UtcTicks;
        var query = Scoped(scope).AsNoTracking()
            .Where(d => d.Status == s_pending && d.ExpiresAtTicks > nowTicks);

        return await PageAsync(DocketCursor.PendingListing, query, page, ct);
    }

    public async Task<DocketPageResult<DocketEntry>> ListApprovedUnexecutedAsync(
        DocketScope scope, DocketPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfLessThan(page.Limit, 1);

        var query = Scoped(scope).AsNoTracking()
            .Where(d => d.Status == s_approved && (d.Execution == null || d.Execution == s_unexecuted));

        return await PageAsync(DocketCursor.ApprovedUnexecutedListing, query, page, ct);
    }

    public async Task<ExpireDueResult> ExpireDueAsync(
        DateTimeOffset now, DocketScope scope, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var nowTicks = now.UtcTicks;

        // limit + 1 candidates, so `more` is answered by this read rather than by a second query.
        var due = await Scoped(scope).AsNoTracking()
            .Where(d => d.Status == s_pending && d.ExpiresAtTicks <= nowTicks)
            .OrderBy(d => d.ExpiresAtTicks)
            .ThenBy(d => d.EntryId)
            .Take(limit + 1)
            .Select(d => d.EntryId)
            .ToListAsync(ct);

        var more = due.Count > limit;
        var won = new List<Guid>(Math.Min(due.Count, limit));

        // One guarded write per row rather than one bulk statement: a bulk UPDATE reports no
        // per-row outcome, so a caller could not tell which rows ITS OWN write transitioned and
        // which a concurrent decision claimed first — and a caller that notified on the latter would
        // double-notify.
        foreach (var entryId in due.Take(limit))
        {
            var rows = await db.Docket
                .Where(d => d.EntryId == entryId && d.Status == s_pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, s_expired)
                    .SetProperty(d => d.DecidedAt, d => (DateTimeOffset?)d.ExpiresAt)
                    .SetProperty(d => d.DecidedAtTicks, d => (long?)d.ExpiresAtTicks), ct);
            if (rows > 0) won.Add(entryId);
        }

        if (won.Count == 0) return new ExpireDueResult([], more);

        var expired = await db.Docket.AsNoTracking()
            .Where(d => won.Contains(d.EntryId))
            .OrderBy(d => d.ExpiresAtTicks)
            .ThenBy(d => d.EntryId)
            .ToListAsync(ct);

        return new ExpireDueResult(expired.Select(ToDomain).ToList(), more);
    }

    public async Task<RetentionResult> ApplyRetentionAsync(
        DocketRetentionPolicy policy, DocketScope scope, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var nowTicks = time.GetUtcNow().UtcTicks;
        var cutoffTicks = policy.OlderThan.UtcTicks;

        var eligible = Scoped(scope).AsNoTracking()
            // Terminal: anything that is not pending, plus a pending row past its deadline, which
            // reads expired whether or not the sweep has reached it.
            .Where(d => d.Status != s_pending || d.ExpiresAtTicks <= nowTicks)
            // Never an approved row whose write has not been reported, however old: it is the only
            // record that a write was authorised and has not happened.
            .Where(d => !(d.Status == s_approved && (d.Execution == null || d.Execution == s_unexecuted)))
            .Where(d => (d.DecidedAtTicks ?? d.ExpiresAtTicks) < cutoffTicks)
            .OrderBy(d => d.CreatedAtTicks)
            .ThenBy(d => d.EntryId);

        var doomed = await eligible.Take(limit + 1).Select(d => d.EntryId).ToListAsync(ct);
        var more = doomed.Count > limit;
        var batch = doomed.Take(limit).ToList();
        if (batch.Count == 0) return new RetentionResult(0, more);

        var removed = await db.Docket.Where(d => batch.Contains(d.EntryId)).ExecuteDeleteAsync(ct);
        return new RetentionResult(removed, more);
    }

    public async Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        return await db.Docket.Where(d => d.TenantId == tenantId).ExecuteDeleteAsync(ct);
    }

    public async IAsyncEnumerable<DocketEntry> ExportAsync(
        DocketScope scope, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var now = time.GetUtcNow();
        var query = Scoped(scope).AsNoTracking()
            .OrderBy(d => d.CreatedAtTicks)
            .ThenBy(d => d.EntryId);

        // AsAsyncEnumerable streams rows off the reader rather than materializing the Docket into a
        // list first, which is what lets a tenant with more rows than fit in memory be exported.
        await foreach (var entity in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return DocketRow.Project(ToDomain(entity), now);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>The Docket narrowed to what <paramref name="scope"/> admits.</summary>
    /// <remarks>
    /// Built by composition rather than as one predicate with null checks in it so the generated SQL
    /// carries only the clauses the scope actually asks for, and so a store-wide scope produces no
    /// tenant clause at all rather than a tautology the planner has to see through.
    /// </remarks>
    private IQueryable<DocketEntryEntity> Scoped(DocketScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IQueryable<DocketEntryEntity> query = db.Docket;
        if (scope.TenantId is { } tenantId) query = query.Where(d => d.TenantId == tenantId);
        if (scope.ConversationId is { } conversationId) query = query.Where(d => d.SessionId == conversationId);
        return query;
    }

    /// <summary>
    /// The row as a caller sees it: scoped, and with the deadline applied.
    /// </summary>
    /// <remarks>
    /// Projected like every other read, so a row whose deadline passed reads expired here too. A
    /// result that handed back the persisted status would be the one path on which a caller learned
    /// a lapsed row was still pending.
    /// </remarks>
    private async Task<DocketEntry?> ReadScopedAsync(Guid entryId, DocketScope scope, CancellationToken ct)
    {
        var entry = await ReadScopedRawAsync(entryId, scope, ct);
        return entry is null ? null : DocketRow.Project(entry, time.GetUtcNow());
    }

    /// <summary>
    /// The row exactly as it is stored, with the deadline NOT applied — for the one caller that
    /// needs to know what the row <em>says</em> rather than what it reads.
    /// </summary>
    private async Task<DocketEntry?> ReadScopedRawAsync(Guid entryId, DocketScope scope, CancellationToken ct)
    {
        var entity = await Scoped(scope).AsNoTracking().FirstOrDefaultAsync(d => d.EntryId == entryId, ct);
        return entity is null ? null : ToDomain(entity);
    }

    /// <summary>
    /// One page of <paramref name="query"/> in filing order, resuming after
    /// <paramref name="page"/>'s cursor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A page boundary never falls inside a filing instant.</b> The cursor carries only that
    /// instant, and the next page asks for rows strictly after it, so two rows filed on the same tick
    /// are always returned together. The alternative — a cursor of (instant, id) with an id
    /// comparison in the <c>WHERE</c> clause — needs a translatable ordering over a GUID column,
    /// which the two providers do not agree on; paging on the instant alone is the shape that is
    /// exactly right on both.
    /// </para>
    /// <para>
    /// The cost is that a page can come back one tick short of its limit, and that a group of rows
    /// sharing one tick is returned whole even when the group is larger than the limit. Neither is
    /// reachable from a real clock, which resolves to 100 ns; both are reachable from a test clock
    /// that does not advance, which is precisely where returning a stable, complete page matters.
    /// </para>
    /// </remarks>
    private async Task<DocketPageResult<DocketEntry>> PageAsync(
        string listing, IQueryable<DocketEntryEntity> query, DocketPage page, CancellationToken ct)
    {
        if (DocketCursor.TryDecode(page.Cursor, listing, out var afterAt, out _))
        {
            var afterTicks = afterAt.UtcTicks;
            query = query.Where(d => d.CreatedAtTicks > afterTicks);
        }

        var ordered = query.OrderBy(d => d.CreatedAtTicks).ThenBy(d => d.EntryId);
        var window = await ordered.Take(page.Limit + 1).ToListAsync(ct);

        var more = window.Count > page.Limit;
        var taken = window.Take(page.Limit).ToList();

        if (more && taken.Count > 0 && window[page.Limit].CreatedAtTicks == taken[^1].CreatedAtTicks)
        {
            // The limit lands inside a filing instant. Trim back to the last instant that fits
            // whole; if the whole window is one instant, take that instant entire and let the page
            // exceed its limit rather than hand out a boundary the next page cannot resume from.
            var boundaryTicks = taken[^1].CreatedAtTicks;
            var trimmed = taken.Where(d => d.CreatedAtTicks != boundaryTicks).ToList();
            if (trimmed.Count > 0)
            {
                taken = trimmed;
            }
            else
            {
                taken = await ordered.Where(d => d.CreatedAtTicks == boundaryTicks).ToListAsync(ct);
                more = await query.AnyAsync(d => d.CreatedAtTicks > boundaryTicks, ct);
            }
        }

        var items = taken.Select(ToDomain).ToList();

        // The boundary comes from the tick COLUMN, not from the round-tripped instant. Postgres
        // stores a timestamp to the microsecond and .NET counts in hundreds of nanoseconds, so a
        // cursor built from the read-back instant can land a fraction before the row it names — and
        // the next page then returns that row a second time.
        var cursor = more && taken.Count > 0
            ? DocketCursor.Encode(
                listing, new DateTimeOffset(taken[^1].CreatedAtTicks, TimeSpan.Zero), taken[^1].EntryId)
            : null;

        return new DocketPageResult<DocketEntry>(items, cursor, more);
    }

    // ── DocketEntry ↔ DocketEntryEntity ─────────────────────────────────────

    internal static DocketEntryEntity ToEntity(DocketEntry entry) => new()
    {
        EntryId = entry.EntryId,
        SessionId = entry.SessionId,
        TenantId = entry.TenantId,
        UserId = entry.UserId,
#pragma warning disable AFFIANT0001 // the alias is still persisted for one release
        ReviewerUserId = entry.ReviewerUserId,
#pragma warning restore AFFIANT0001
        OperationType = entry.OperationType,
        ToolName = entry.ToolName,
        Channel = entry.Channel,
        Requirement = entry.Requirement.ToString(),
        AffidavitJson = JsonSerializer.Serialize(entry.Envelope, s_jsonOptions),
        ProvenanceChainsJson = SerializeProvenanceChains(entry.Envelope.Fields),
        AmendmentsJson = DocketRowSerialization.WriteAmendments(entry.Amendments),
        AmendedAffidavitJson = entry.AmendedAffidavit is null
            ? null
            : JsonSerializer.Serialize(entry.AmendedAffidavit, s_jsonOptions),
        AmendedProvenanceChainsJson = entry.AmendedAffidavit is null
            ? null
            : SerializeProvenanceChains(entry.AmendedAffidavit.Fields),
        CreatedAt = entry.CreatedAt,
        CreatedAtTicks = entry.CreatedAt.UtcTicks,
        ExpiresAt = entry.ExpiresAt,
        ExpiresAtTicks = entry.ExpiresAt.UtcTicks,
        DecidedAt = entry.DecidedAt,
        DecidedAtTicks = entry.DecidedAt?.UtcTicks,
        Status = entry.Status.ToString(),
        Execution = entry.Execution?.ToString(),
        ExecutionDetail = entry.ExecutionDetail,
        DecisionJson = DocketRowSerialization.WriteDecision(entry.Decision),
        AttestationJson = DocketRowSerialization.WriteAttestation(entry.Attestation),
        BlockedJson = DocketRowSerialization.WriteBlocked(entry.Blocked),
        CompositeRef = entry.CompositeRef,
        PreservedAmendmentsJson = DocketRowSerialization.WritePreservedAmendments(entry.PreservedAmendments),
        Supersedes = entry.Supersedes,
        ResubmittedTo = entry.ResubmittedTo,
        ProtocolVersion = entry.ProtocolVersion
    };

    internal static DocketEntry ToDomain(DocketEntryEntity entity)
    {
        var affidavit = Rehydrate(entity.AffidavitJson, entity.ProvenanceChainsJson)!;
        var amended = Rehydrate(entity.AmendedAffidavitJson, entity.AmendedProvenanceChainsJson);

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
            Amendments: DocketRowSerialization.ReadAmendments(entity.AmendmentsJson),
            ResubmittedTo: entity.ResubmittedTo,
            Execution: entity.Execution is null ? null : Enum.Parse<ExecutionOutcome>(entity.Execution),
            ExecutionDetail: entity.ExecutionDetail,
            Decision: DocketRowSerialization.ReadDecision(entity.DecisionJson),
            Attestation: DocketRowSerialization.ReadAttestation(entity.AttestationJson),
            Blocked: DocketRowSerialization.ReadBlocked(entity.BlockedJson),
            CompositeRef: entity.CompositeRef,
            AmendedAffidavit: amended,
            PreservedAmendments: DocketRowSerialization.ReadPreservedAmendments(entity.PreservedAmendmentsJson),
            Supersedes: entity.Supersedes,
            DecidedAt: entity.DecidedAt,
            ProtocolVersion: string.IsNullOrEmpty(entity.ProtocolVersion)
                ? AffiantProtocol.Version
                : entity.ProtocolVersion)
        {
            // Null on a row filed before the column existed, where OperationType carries the same
            // fact; the row's ToolName property falls back to it, so this stays correct either way.
            ToolName = entity.ToolName ?? entity.OperationType,
            Channel = entity.Channel,
            Requirement = Enum.TryParse<ReviewRequirement>(entity.Requirement, out var requirement)
                ? requirement
                : ReviewRequirement.ReviewerConfirmation
        };
    }

    /// <summary>
    /// The stored Affidavit, with its provenance chains re-attached and its field values read back
    /// as the CLR values they were filed as.
    /// </summary>
    /// <remarks>
    /// <see cref="AffidavitField.Value"/> is <c>object?</c>, so a straight deserialization hands
    /// every field back as a raw JSON element and never as the number, string or boolean the
    /// projection put there. A host risk scorer that pattern-matches on the value's type would then
    /// see an unrecognised type for every field of every row that came out of a store, and the same
    /// content would score one way when first filed and another way when read back — which is the
    /// path a resubmission always takes. <see cref="AffidavitFieldValues"/> closes that here, at the
    /// store boundary, so no caller has to remember to.
    /// </remarks>
    private static Affidavit? Rehydrate(string? affidavitJson, string? chainsJson)
    {
        if (string.IsNullOrEmpty(affidavitJson)) return null;

        var affidavit = JsonSerializer.Deserialize<Affidavit>(affidavitJson, s_jsonOptions);
        if (affidavit is null) return null;

        var chains = DeserializeProvenanceChains(chainsJson);
        var fields = affidavit.Fields
            .Select(f => chains.TryGetValue(f.Name, out var chain) ? f with { Provenance = chain } : f)
            .ToArray();
        return AffidavitFieldValues.Typed(affidavit with { Fields = fields });
    }

    private static string SerializeProvenanceChains(AffidavitField[] fields)
    {
        var dict = fields.ToDictionary(f => f.Name, f => f.Provenance);
        return JsonSerializer.Serialize(dict, s_jsonOptions);
    }

    private static Dictionary<string, ProvenanceChain> DeserializeProvenanceChains(string? json)
    {
        if (string.IsNullOrEmpty(json) || json is "[]" or "{}")
            return new Dictionary<string, ProvenanceChain>();

        return JsonSerializer.Deserialize<Dictionary<string, ProvenanceChain>>(json, s_jsonOptions)
               ?? new Dictionary<string, ProvenanceChain>();
    }
}
