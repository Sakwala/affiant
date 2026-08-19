namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

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
    Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct);

    /// <summary>
    /// Returns all pending entries whose <see cref="DocketEntry.ExpiresAt"/> is on or before
    /// <paramref name="expiresBeforeUtc"/>. Used by <c>DocketExpiryService</c> to identify
    /// rows to bulk-expire each tick.
    /// </summary>
    Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct);

    /// <summary>
    /// Bulk-transitions the specified entries from <see cref="ReviewStatus.Pending"/> to
    /// <see cref="ReviewStatus.Expired"/>. Idempotent — entries that are no longer Pending
    /// are silently skipped (the <c>WHERE Status = 'Pending'</c> guard applies).
    /// </summary>
    Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct);
}
