namespace Affiant.Transport.SignalR.Hubs;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Abstract base for all Affiant SignalR hubs. Provides group-based session management,
/// session rehydration, and broadcaster helpers. Subclasses supply JWT extraction and
/// domain-specific hub method handlers.
/// </summary>
public abstract class AffiantHub(IChatSessionStore chatSessionStore) : Hub
{
    protected IChatSessionStore ChatSessionStore { get; } = chatSessionStore;

    public override Task OnConnectedAsync() => base.OnConnectedAsync();

    public override Task OnDisconnectedAsync(Exception? exception) => base.OnDisconnectedAsync(exception);

    /// <summary>
    /// Returns the SignalR group name for the given session. Override to apply a custom prefix.
    /// </summary>
    protected virtual string GetSessionGroupName(string sessionId) => sessionId;

    /// <summary>
    /// Returns the SignalR group name for routing Evidence Card requests to a reviewer.
    /// </summary>
    protected virtual string GetReviewerGroupName(string reviewerId) => $"reviewer:{reviewerId}";

    protected Task AddToSessionGroupAsync(string sessionId, CancellationToken cancellationToken = default)
        => Groups.AddToGroupAsync(Context.ConnectionId, GetSessionGroupName(sessionId), cancellationToken);

    protected Task RemoveFromSessionGroupAsync(string sessionId, CancellationToken cancellationToken = default)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, GetSessionGroupName(sessionId), cancellationToken);

    protected Task AddToReviewerGroupAsync(string reviewerId, CancellationToken cancellationToken = default)
        => Groups.AddToGroupAsync(Context.ConnectionId, GetReviewerGroupName(reviewerId), cancellationToken);

    protected Task RemoveFromReviewerGroupAsync(string reviewerId, CancellationToken cancellationToken = default)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, GetReviewerGroupName(reviewerId), cancellationToken);

    protected Task BroadcastToSessionAsync(string sessionId, string method, object? data = null, CancellationToken cancellationToken = default)
    {
        var group = Clients.Group(GetSessionGroupName(sessionId));
        return data is null
            ? group.SendAsync(method, cancellationToken)
            : group.SendAsync(method, data, cancellationToken);
    }

    protected Task BroadcastToReviewerAsync(string reviewerId, string method, object? data = null, CancellationToken cancellationToken = default)
    {
        var group = Clients.Group(GetReviewerGroupName(reviewerId));
        return data is null
            ? group.SendAsync(method, cancellationToken)
            : group.SendAsync(method, data, cancellationToken);
    }

    /// <summary>
    /// Opens a canonical <c>invoke_agent</c> span on the <c>Affiant.Framework</c> activity source
    /// and returns it. The caller is responsible for disposing the returned activity (typically
    /// via a <c>using</c> declaration) when the agent turn completes.
    ///
    /// Call this at the top of every hub method that triggers an agent invocation (e.g.,
    /// <c>SendMessage</c>). All <c>execute_tool</c> spans emitted by <c>ToolTracingFilter</c>
    /// during the turn will be children of this span.
    /// </summary>
    /// <param name="conversationId">The session / conversation identifier. Recorded as <c>gen_ai.conversation.id</c>.</param>
    /// <param name="userIntent">Optional: the raw user message. Truncated to 256 chars for the <c>affiant.user.intent</c> tag.</param>
    protected static Activity? BeginAgentTurn(string conversationId, string? userIntent = null)
    {
        var activity = AffiantTelemetry.AffiantActivitySource.StartActivity("invoke_agent", ActivityKind.Internal);
        activity?.SetTag("gen_ai.conversation.id", conversationId);
        if (userIntent is not null)
            activity?.SetTag("affiant.user.intent", userIntent.Length > 256 ? userIntent[..256] : userIntent);
        return activity;
    }

    /// <summary>
    /// Adds the current connection to the session group and loads the persisted chat messages.
    /// Call this from the subclass's reconnection flow.
    /// </summary>
    protected async Task<IReadOnlyList<AffiantChatMessage>> RehydrateSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await AddToSessionGroupAsync(sessionId, cancellationToken);
        return await ChatSessionStore.LoadMessagesAsync(sessionId, cancellationToken);
    }
}
