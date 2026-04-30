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
    /// Implementations must enforce optimistic concurrency: only entries currently
    /// in Pending status may be transitioned; racing writers must detect the conflict.
    /// </summary>
    Task UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct);

    Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct);
}

/// <summary>
/// Domain-agnostic accumulated entity state for a session.
/// Hosts encode domain-specific data (e.g., aircraft, parts) as EntityRef objects
/// in the Entities dictionary. Keys are stable entity identifiers.
/// </summary>
public sealed record ConversationContext(
    string SessionId,
    Dictionary<string, EntityRef> Entities);
