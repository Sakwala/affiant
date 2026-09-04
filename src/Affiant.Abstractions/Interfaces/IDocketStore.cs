namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// The framework's review ledger: durable storage for pending, approved, rejected and expired
/// <see cref="DocketEntry"/> records, plus the per-session <see cref="ConversationContext"/> the
/// review was raised against. Implement it to back the docket with your own store; ship-ready
/// implementations for in-memory, SQLite and PostgreSQL come with <c>Affiant.Docket</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is a correctness-critical contract, not a CRUD wrapper. Two members carry explicit
/// atomicity obligations that <c>ReviewGate</c> relies on and that a naive read-then-write
/// implementation will violate: <see cref="UpdateReviewStatusAsync"/> (double-submit prevention)
/// and <see cref="ConsumeForResubmitAsync"/> (double-resubmit prevention). Both express their
/// result as rows-affected — 1 means this caller won, 0 means someone else already did — so the
/// guard must live in the write itself, never in surrounding C#. Read each member's remarks before
/// implementing; the per-member contracts are the specification.
/// </para>
/// <para>
/// A store is expected to be safe for concurrent use across sessions and across callers within a
/// session. <c>Affiant.Testing.ComplianceHarness</c> exercises these invariants against any
/// implementation, including the ordering contract on
/// <see cref="ListPendingBySessionAsync"/>.
/// </para>
/// </remarks>
public interface IDocketStore
{
    /// <summary>
    /// Persist the accumulated entity state for a session.
    /// The ConversationContext captures domain-agnostic EntityRef objects; host adapters
    /// are responsible for encoding domain-specific state into the EntityRef dictionary.
    /// </summary>
    Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct);

    /// <summary>Load a session's entity state. Returns null for sessions with no persisted context.</summary>
    Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// File a new DocketEntry into the review queue.
    /// Implementations must enforce an idempotency guard on EntryId:
    /// a second call with the same EntryId is a no-op.
    /// </summary>
    Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct);

    /// <summary>
    /// Reads one entry, or <c>null</c> when <paramref name="entryId"/> names no entry.
    /// </summary>
    /// <remarks>
    /// <para><strong>Expiry is a queryable state, not a swept-in one.</strong></para>
    /// <para>
    /// An entry whose persisted status is <see cref="ReviewStatus.Pending"/> but whose
    /// <see cref="DocketEntry.ExpiresAt"/> is on or before the current instant MUST be returned
    /// with <see cref="DocketEntry.Status"/> = <see cref="ReviewStatus.Expired"/>, whether or not
    /// an expiry sweep has run. The boundary is inclusive: at exactly
    /// <see cref="DocketEntry.ExpiresAt"/> the entry is expired.
    /// </para>
    /// <para>
    /// The projection is a read-time one — it does not write. The sweep
    /// (<c>Affiant.Docket.Services.DocketExpiryService</c>) still persists
    /// <see cref="ReviewStatus.Expired"/> onto the row, and the guarded
    /// <see cref="UpdateReviewStatusAsync"/> / <see cref="ConsumeForResubmitAsync"/> writes still
    /// test the <em>persisted</em> status, so a caller that needs the transition durably recorded
    /// (a resubmission, an audit read after a restart) must still let the sweep — or its own
    /// <see cref="UpdateReviewStatusAsync"/> call — commit it.
    /// </para>
    /// <para>
    /// Implementations read the current instant from an injected <see cref="TimeProvider"/>, never
    /// from <c>DateTimeOffset.UtcNow</c>, so a test can move the clock.
    /// </para>
    /// </remarks>
    Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct);

    /// <summary>
    /// Transition a DocketEntry's review status.
    /// </summary>
    /// <returns>
    /// The number of rows affected (1 on success, 0 if the entry was not in
    /// <see cref="ReviewStatus.Pending"/> state — see remarks for the double-submit contract).
    /// </returns>
    /// <remarks>
    /// <para><strong>Double-Submit Prevention Contract:</strong></para>
    /// <para>
    /// Implementations MUST enforce atomic read-before-write semantics.
    /// The update MUST include a guard condition equivalent to <c>WHERE Status = 'Pending'</c>
    /// so that a second update attempt on the same <paramref name="entryId"/> (already
    /// approved/rejected/expired) results in 0 rows affected rather than a second transition.
    /// </para>
    /// <para>
    /// <c>ReviewGate</c> relies on this invariant: if <c>UpdateReviewStatusAsync</c> returns
    /// 0, the entry is no longer pending and the gate handles it idempotently.
    /// </para>
    /// </remarks>
    Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct);

    /// <summary>
    /// Atomically claims <paramref name="entryId"/> for resubmission by recording
    /// <paramref name="newEntryId"/> onto its <see cref="DocketEntry.ResubmittedTo"/> — the race
    /// guard and lineage record for <c>ReviewGate.ResubmitAsync</c> (Area-5 Decision 2, affiant#31).
    /// </summary>
    /// <returns>
    /// The number of rows affected (1 if this call won the claim, 0 if <paramref name="entryId"/>
    /// was not found, is not <see cref="ReviewStatus.Expired"/>, or a concurrent caller already
    /// claimed it — see remarks for the double-resubmit contract).
    /// </returns>
    /// <remarks>
    /// <para><strong>Double-Resubmit Prevention Contract:</strong></para>
    /// <para>
    /// Implementations MUST enforce atomic read-before-write semantics with a guard condition
    /// equivalent to <c>WHERE Status = 'Expired' AND ResubmittedTo IS NULL</c> — the same
    /// 0/1-rows-affected CAS idiom <see cref="UpdateReviewStatusAsync"/> uses for its own guard —
    /// so two concurrent calls for the same <paramref name="entryId"/> can never both succeed.
    /// </para>
    /// <para>
    /// Unlike <see cref="UpdateReviewStatusAsync"/>, this does not transition <see cref="DocketEntry.Status"/>:
    /// there is no <c>ReviewStatus.Resubmitted</c> by design (Area-5 Decision 2). The entry stays
    /// <see cref="ReviewStatus.Expired"/>; <see cref="DocketEntry.ResubmittedTo"/> alone records that
    /// it was superseded and by which new entry.
    /// </para>
    /// </remarks>
    Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct);

    /// <summary>
    /// Reverse lookup for resubmission lineage: finds the <see cref="DocketEntry"/> whose
    /// <see cref="DocketEntry.ResubmittedTo"/> equals <paramref name="entryId"/> — i.e. the expired
    /// entry that <paramref name="entryId"/> was resubmitted from, if any.
    /// </summary>
    /// <returns>The parent entry, or <c>null</c> if <paramref name="entryId"/> was not itself produced by a resubmission.</returns>
    /// <remarks>
    /// Closes the silent-loss window in <see cref="Transport.EvidenceCardRequest.PriorAmendments"/>:
    /// that field only ever travels on the transient resubmission broadcast, never onto the new
    /// entry's own <see cref="DocketEntry.Amendments"/>. A reconnect that arrives after the broadcast
    /// was already consumed (or missed) uses this lookup to re-derive the same
    /// <see cref="DocketEntry.Amendments"/> from the parent — see
    /// <c>SessionRehydrator.RehydrateAsync</c>.
    /// </remarks>
    Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct);

    /// <summary>
    /// Persist the reviewer's amendments onto a <see cref="DocketEntry"/> — the field values a
    /// human reviewer changed while acting on an Evidence Card (issue #6, the amendment
    /// round-trip). Overwrites any amendments previously recorded on the entry (e.g. from
    /// <see cref="ReviewContext.Amendments"/> at filing time).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Framework responsibility ends at persistence. Appending
    /// <see cref="ProvenanceTag"/> UserStated tags to each amended field's
    /// <see cref="ProvenanceChain"/> before the write reaches the domain store is the host's
    /// <see cref="IWriteExecutor"/> overlay's job — <c>IWriteExecutor.ExecuteAsync</c> already
    /// accepts the amendments dictionary for exactly that purpose.
    /// </para>
    /// <para><strong>No status guard — deliberate, do not add one.</strong></para>
    /// <para>
    /// Unlike <see cref="UpdateReviewStatusAsync"/>, this write carries no
    /// <c>WHERE Status = 'Pending'</c> (or any other status) condition on any of the three
    /// backends. That is intentional, not an oversight: <c>ReviewGate.HandleDecisionAsync</c>'s
    /// restart path persists a reviewer's edits onto entries that are already
    /// <see cref="ReviewStatus.Expired"/> — or otherwise no longer <c>Pending</c> — by the time a
    /// late decision replays (late-amendment preservation, issue #8). A status-guarded write would
    /// silently discard exactly the edits this method exists to preserve. Amendments are
    /// non-terminal, append-only reviewer data, not a status transition, so last-write-wins
    /// against any entry state is the framework's accepted conservatism here.
    /// </para>
    /// </remarks>
    Task UpdateAmendmentsAsync(
        Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct);

    /// <summary>
    /// Returns every <see cref="ReviewStatus.Pending"/> entry for <paramref name="sessionId"/>,
    /// ordered by <see cref="DocketEntry.CreatedAt"/> ascending — oldest-filed entry first (Area-5
    /// Decision 3 / P2d rider, affiant#28). Both <c>SessionRehydrator</c> and
    /// <c>ReviewGate.RebroadcastPendingCardsAsync</c> rely on this order to replay a session's
    /// stranded reviews in the sequence they were originally filed.
    /// </summary>
    /// <remarks>
    /// Expiry is a queryable state (see <see cref="GetDocketEntryAsync"/>): an entry whose
    /// <see cref="DocketEntry.ExpiresAt"/> is on or before the current instant is no longer pending
    /// and MUST NOT appear here, swept or not. That is also what keeps a lapsed entry from being
    /// rehydrated as pending on reconnect.
    /// </remarks>
    Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Returns every <see cref="ReviewStatus.Pending"/> entry across all sessions, in no specified
    /// order — the listing primitive <c>DocketExpiryService</c>'s sweep uses to re-broadcast
    /// <see cref="Transport.TransportEvent.EvidenceCardRequest"/> unconditionally each tick,
    /// independent of whether the entry's filing-time broadcast reported success (Area-5 Decision 3,
    /// affiant#28). Unlike <see cref="ListPendingBySessionAsync"/>, this is not scoped to a session
    /// and carries no ordering contract — callers that need a stable order per session should use
    /// that method instead.
    /// </summary>
    /// <remarks>
    /// Expiry is a queryable state (see <see cref="GetDocketEntryAsync"/>): an entry whose
    /// <see cref="DocketEntry.ExpiresAt"/> is on or before the current instant is no longer pending
    /// and MUST NOT appear here, swept or not — so the sweep's own re-broadcast phase never
    /// re-broadcasts a card for an entry that has already run out of time.
    /// </remarks>
    Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct);

    /// <summary>
    /// Returns at most <paramref name="limit"/> entries whose persisted status is still
    /// <see cref="ReviewStatus.Pending"/> and whose <see cref="DocketEntry.ExpiresAt"/> is on or
    /// before <paramref name="expiresBeforeUtc"/>, oldest deadline first. Used by
    /// <c>DocketExpiryService</c> to identify the rows one tick expires.
    /// </summary>
    /// <param name="expiresBeforeUtc">The instant to compare deadlines against — inclusive.</param>
    /// <param name="limit">
    /// The maximum number of entries to return. Must be greater than zero. This is the page size
    /// one sweep tick works through, so a backlog is drained across ticks instead of loaded whole.
    /// </param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// <para>
    /// This is the one read that deliberately reports the <em>persisted</em> status rather than the
    /// read-time projection <see cref="GetDocketEntryAsync"/> applies: its whole purpose is to find
    /// rows whose <see cref="ReviewStatus.Expired"/> transition has not been committed yet.
    /// </para>
    /// <para>
    /// Ordering by <see cref="DocketEntry.ExpiresAt"/> ascending is what makes the paging fair —
    /// without it, a store free to return any <paramref name="limit"/> rows could starve the oldest
    /// entries indefinitely.
    /// </para>
    /// <para>
    /// <strong>Not yet the full paging contract.</strong> The Affiant protocol's DK-3 asks for
    /// <c>expireDue(now, scope, limit)</c> — scoped to a tenant or conversation and reporting
    /// whether more entries remain — plus an opaque cursor on every list this interface exposes.
    /// This release adds the bound only; the scope argument, the more-remain signal and the cursors
    /// land with the docket change that reshapes <see cref="DocketEntry"/> itself.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(
        DateTimeOffset expiresBeforeUtc, int limit, CancellationToken ct);

    /// <summary>
    /// Bulk-transitions the specified entries from <see cref="ReviewStatus.Pending"/> to
    /// <see cref="ReviewStatus.Expired"/>. Idempotent — entries that are no longer Pending
    /// are silently skipped (the <c>WHERE Status = 'Pending'</c> guard applies).
    /// </summary>
    Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct);
}
