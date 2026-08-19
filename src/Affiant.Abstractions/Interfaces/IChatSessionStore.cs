namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IChatSessionStore
{
    Task<ChatSession> CreateAsync(string tenantId, string userId, CancellationToken ct);
    Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Rehydration-class write: replaces every message stored for <paramref name="sessionId"/> with
    /// <paramref name="messages"/> in full. Use when the caller already holds (or has just
    /// reconstructed) the complete, authoritative message list — e.g. after a truncation/reduction
    /// pass on reconnect.
    /// </summary>
    /// <remarks>
    /// Not append-safe: implementations may delete-and-reinsert to realize the replace, so a second
    /// concurrent call for the same <paramref name="sessionId"/> working from a stale snapshot can
    /// silently drop the first caller's messages (affiant#27, the structural loss window this
    /// contract accepts for full-replace writes). Turn-by-turn persistence — the common case — must
    /// use <see cref="AppendMessagesAsync"/> instead, which does not carry this risk.
    /// </remarks>
    Task SaveMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct);

    /// <summary>
    /// Turn-save-class write: adds <paramref name="messages"/> after every message already stored
    /// for <paramref name="sessionId"/>, without reading, deleting, or reinserting existing rows.
    /// Use for persisting one model turn's new messages.
    /// </summary>
    /// <remarks>
    /// Implementations must assign each new message an ordinal continuing at
    /// (current MAX ordinal for the session) + 1, and must perform the ordinal computation and the
    /// insert as one transaction — never by reusing <see cref="SaveMessagesAsync"/>'s delete-and-
    /// reinsert path, which is exactly the affiant#27 loss window this member exists to avoid.
    /// A call with an empty <paramref name="messages"/> is a no-op. This member does not itself
    /// serialize concurrent callers for the same <paramref name="sessionId"/> — two overlapping
    /// calls can still race on the MAX-ordinal read (Area-5 Decision-record residual, tracked for a
    /// per-session lock primitive); it only guarantees it never re-touches or discards a row that
    /// was already durable when the call began, which is what <see cref="SaveMessagesAsync"/> cannot
    /// promise.
    /// </remarks>
    Task AppendMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct);

    Task<IReadOnlyList<AffiantChatMessage>> LoadMessagesAsync(string sessionId, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);
}
