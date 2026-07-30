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
    /// Persist the reviewer's amendments onto a <see cref="DocketEntry"/> — the field values a
    /// human reviewer changed while acting on an Evidence Card (issue #6, the amendment
    /// round-trip). Overwrites any amendments previously recorded on the entry (e.g. from
    /// <see cref="ReviewContext.Amendments"/> at filing time).
    /// </summary>
    /// <remarks>
    /// Framework responsibility ends at persistence. Appending
    /// <see cref="ProvenanceTag"/> UserStated tags to each amended field's
    /// <see cref="ProvenanceChain"/> before the write reaches the domain store is the host's
    /// <see cref="IWriteExecutor"/> overlay's job — <c>IWriteExecutor.ExecuteAsync</c> already
    /// accepts the amendments dictionary for exactly that purpose.
    /// </remarks>
    Task UpdateAmendmentsAsync(
        Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct);

    Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct);

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

/// <summary>
/// Domain-agnostic accumulated entity state for a session.
/// Hosts encode domain-specific data (e.g., aircraft, parts) as EntityRef objects
/// in the Entities dictionary. Keys are stable entity identifiers.
/// </summary>
public sealed record ConversationContext(
    string SessionId,
    Dictionary<string, EntityRef> Entities);
