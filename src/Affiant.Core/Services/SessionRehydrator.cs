using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Affiant.Core.Services;

/// <summary>
/// Reconstructs full conversation state on reconnect: <see cref="ChatHistory"/>,
/// the abstract <see cref="ConversationContext"/>, and pending <see cref="DocketEntry"/> entries.
/// Applies <c>ChatHistoryTruncationReducer</c> when the message count exceeds the configurable
/// threshold (<c>Affiant:ContextWindow:MessageLimit</c>).
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

    public async Task<RehydrationResult> RehydrateAsync(string sessionId, CancellationToken ct)
    {
        // 1. Load messages and build ChatHistory
        var messages = await chatStore.LoadMessagesAsync(sessionId, ct);
        var history = new ChatHistory();
        foreach (var msg in messages)
            history.Add(msg);

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

        // 4. Load pending Docket entries
        var pendingEntries = await docketStore.ListPendingBySessionAsync(sessionId, ct);

        return new RehydrationResult(history, context, pendingEntries);
    }
}

/// <summary>
/// Result of <see cref="SessionRehydrator.RehydrateAsync"/> — carries everything
/// needed to resume a conversation: the (possibly truncated) chat history, the
/// domain-agnostic conversation context (nullable for pre-migration sessions),
/// and any pending Docket entries awaiting review.
/// </summary>
public sealed record RehydrationResult(
    ChatHistory History,
    ConversationContext? Context,
    IReadOnlyList<DocketEntry> PendingEntries);
