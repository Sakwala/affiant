namespace Affiant.SemanticKernel.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

/// <summary>
/// Reconstructs full conversation state on reconnect: <see cref="ChatHistory"/>,
/// the abstract <see cref="ConversationContext"/>, and pending <see cref="DocketEntry"/> entries.
/// Applies <c>ChatHistoryTruncationReducer</c> when the message count exceeds the configurable
/// threshold (<c>Affiant:ContextWindow:MessageLimit</c>).
///
/// Lives in the Semantic Kernel adapter because it materializes an SK <see cref="ChatHistory"/> and
/// uses SK's truncation reducer. Neutral <see cref="AffiantChatMessage"/> loaded from the store is
/// converted to <see cref="ChatHistory"/> at this edge.
///
/// Invoked from the host's hub/gateway when a client reconnects with an existing session id.
/// </summary>
public sealed class SessionRehydrator(
    IChatSessionStore chatStore,
    IDocketStore docketStore,
    IConfiguration config,
    ILogger<SessionRehydrator> logger)
{
    private const int DefaultMessageLimit = 50;

    /// <summary>How many Docket entries one rehydration reads at a time.</summary>
    private const int DocketPageSize = 50;

    /// <summary>
    /// Rebuilds a session without naming its tenant — every release before the Docket became
    /// tenant-scoped had this shape, and a session id is unique across tenants, so it still resolves
    /// the same rows.
    /// </summary>
    /// <param name="sessionId">The session to rebuild.</param>
    /// <param name="ct">Caller cancellation.</param>
    public Task<RehydrationResult> RehydrateAsync(string sessionId, CancellationToken ct)
        => RehydrateAsync(sessionId, tenantId: null, ct);

    /// <summary>
    /// Rebuilds a session, reading its Docket in the fixed rehydration order: pending entries first,
    /// then approved entries whose write has not been reported, each in filing order and paged.
    /// </summary>
    /// <param name="sessionId">The session to rebuild.</param>
    /// <param name="tenantId">The tenant the session belongs to, when the caller knows it.</param>
    /// <param name="ct">Caller cancellation.</param>
    public async Task<RehydrationResult> RehydrateAsync(
        string sessionId, string? tenantId, CancellationToken ct)
    {
        // 1. Load messages and build ChatHistory
        var messages = await chatStore.LoadMessagesAsync(sessionId, ct);
        var history = SkMessageConversions.ToChatHistory(messages);

        logger.LogInformation("Rehydrating session {SessionId}: {Count} messages loaded", sessionId, messages.Count);

        // 2. Apply truncation if over limit
        var limit = int.TryParse(config["Affiant:ContextWindow:MessageLimit"], out var parsed)
            ? parsed
            : DefaultMessageLimit;
        if (history.Count > limit)
        {
#pragma warning disable SKEXP0001 // ChatHistoryTruncationReducer is experimental
            var reducer = new ChatHistoryTruncationReducer(limit);
            var reduced = await reducer.ReduceAsync(history, ct);
            if (reduced is not null)
            {
                history = new ChatHistory(reduced);
                logger.LogInformation("Truncated session {SessionId} from {Original} to {Truncated} messages",
                    sessionId, messages.Count, history.Count);
            }
#pragma warning restore SKEXP0001
        }

        // 3. Load ConversationContext (null-safe for pre-migration sessions)
        var context = await docketStore.LoadContextAsync(sessionId, ct);

        // 4. Load the Docket in the order a reconnecting client must see it: what still needs a
        // decision before what still needs execution. The two groups answer different questions and
        // a client that interleaved them would put already-agreed work in front of work that is
        // still blocked on the reader.
        var scope = new DocketScope(tenantId, sessionId);
        var docketEntries = await DocketRehydration.AllAsync(docketStore, scope, DocketPageSize, ct);
        var pendingEntries = docketEntries
            .Where(e => e.Status == ReviewStatus.Pending)
            .ToList();
        var approvedUnexecuted = docketEntries
            .Where(e => e.Status == ReviewStatus.Approved)
            .ToList();

        // 5. Re-derive PriorAmendments for any pending entry produced by a resubmission (Area-5
        // Decision 2, affiant#31 D2 acceptance criterion 4). EvidenceCardRequest.PriorAmendments
        // only ever travels on the transient resubmission broadcast, never onto the new entry's
        // own DocketEntry.Amendments — a reconnect that arrives after that broadcast was already
        // consumed (or missed) would otherwise silently lose "what the reviewer already agreed to."
        var priorAmendments = new Dictionary<Guid, IReadOnlyDictionary<string, object?>>();
        foreach (var pendingEntry in pendingEntries)
        {
            var parent = await docketStore.GetResubmissionParentAsync(pendingEntry.EntryId, ct);
            if (parent?.Amendments is { Count: > 0 })
                priorAmendments[pendingEntry.EntryId] = parent.Amendments;
        }

        return new RehydrationResult(history, context, pendingEntries, priorAmendments)
        {
            ApprovedUnexecutedEntries = approvedUnexecuted
        };
    }
}

/// <summary>
/// Result of <c>SessionRehydrator.RehydrateAsync</c> — carries everything
/// needed to resume a conversation: the (possibly truncated) chat history, the
/// domain-agnostic conversation context (nullable for pre-migration sessions),
/// and any pending Docket entries awaiting review.
/// </summary>
/// <param name="PriorAmendmentsByEntryId">
/// For each entry in <see cref="PendingEntries"/> that was itself produced by
/// <c>ReviewGate.ResubmitAsync</c>, the amendments a reviewer made on the expired entry it
/// superseded — re-derived via <see cref="Affiant.Abstractions.Interfaces.IDocketStore.GetResubmissionParentAsync"/>
/// since <see cref="EvidenceCardRequest.PriorAmendments"/> only ever travels on the original,
/// transient resubmission broadcast. Entries not produced by a resubmission, or whose parent had
/// no amendments, are absent from this dictionary — never present with a null or empty value.
/// </param>
public sealed record RehydrationResult(
    ChatHistory History,
    ConversationContext? Context,
    IReadOnlyList<DocketEntry> PendingEntries,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> PriorAmendmentsByEntryId)
{
    /// <summary>
    /// Entries this session had approved whose write the host's executor has not reported on, in
    /// filing order — the second half of the rehydration sequence.
    /// </summary>
    /// <remarks>
    /// An approved write nobody has reported on is work outstanding, and after a restart this is the
    /// only record that the work exists. It is a separate list from
    /// <see cref="PendingEntries"/> rather than appended to it because the two need different things
    /// from the client: a decision, and an execution.
    /// </remarks>
    public IReadOnlyList<DocketEntry> ApprovedUnexecutedEntries { get; init; } = [];
}
