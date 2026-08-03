namespace Affiant.Transport.SignalR.Hubs;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Observability;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Abstract base for all Affiant SignalR hubs. Provides group-based session management,
/// session rehydration, and broadcaster helpers. Subclasses supply JWT extraction and
/// domain-specific hub method handlers.
/// </summary>
/// <remarks>
/// P5c (area-4 d1-host-bypass finding D): before this fix, <c>BroadcastToSessionAsync</c>/
/// <c>BroadcastToReviewerAsync</c> took a raw <c>string method</c> and called
/// <c>Clients.Group(...).SendAsync(method, ...)</c> directly — the framework's OWN hub base
/// bypassing its OWN <see cref="TransportEvent"/>/<see cref="TransportEventExtensions.ToClientEventName"/>
/// abstraction, exactly the pattern both hosts' hot-path token streaming does for the same
/// structural reason (no need to thread a <c>connectionId</c>/DI-resolve <see cref="IStreamingTransport"/>
/// separately when <c>Clients</c> is already in scope). Now both helpers take a typed
/// <see cref="TransportEvent"/> and route through the injected <see cref="Transport"/>, so a rename
/// of the wire method name lives in exactly one place
/// (<see cref="TransportEventExtensions.ToClientEventName"/>) instead of also needing every hub
/// subclass's string literals updated. <see cref="Transport"/> is also exposed directly so a
/// subclass's own connection-scoped sends (e.g. per-token streaming to <c>Context.ConnectionId</c>)
/// no longer need a second, redundant DI resolution of <see cref="IStreamingTransport"/> — the base
/// class already has it.
/// </remarks>
/// <remarks>
/// P4 (area-4, ruled 2026-08-04): now <c>Hub&lt;IAffiantHubClient&gt;</c> instead of the untyped
/// <c>Hub</c> — <c>Clients.Caller</c>/<c>Clients.Group(...)</c>/etc. inside any hub subclass are
/// strongly typed against <see cref="IAffiantHubClient"/>, whose method names are locked to
/// <see cref="TransportEventExtensions.ToClientEventName"/>'s outputs. A subclass streaming a token
/// now calls <c>Clients.Caller.ReceiveToken(chunk)</c> — compiler-checked — instead of the raw,
/// typo-able <c>Clients.Caller.SendAsync("ReceiveToken", chunk)</c> string literal both reference
/// hosts' hot-path streaming code used exclusively before this change. This is a C#-side-only
/// safety net (no TypeScript is generated or constrained) and applies only to calls a hub subclass
/// makes directly through its own <c>Clients</c> property — <see cref="IStreamingTransport"/>
/// (used by <see cref="Transport"/> and by every framework service broadcasting from outside a hub
/// context) stays deliberately untyped; see <see cref="IAffiantHubClient"/>'s own remarks for why.
/// </remarks>
public abstract class AffiantHub(IChatSessionStore chatSessionStore, IStreamingTransport transport)
    : Hub<IAffiantHubClient>
{
    protected IChatSessionStore ChatSessionStore { get; } = chatSessionStore;

    /// <summary>
    /// The same <see cref="IStreamingTransport"/> singleton the framework's own review/docket
    /// services broadcast through — available here so a hub subclass's connection- or
    /// group-scoped sends (including per-token streaming) can go through the one typed
    /// abstraction instead of reaching for <c>Clients</c> directly with a raw method-name string.
    /// </summary>
    protected IStreamingTransport Transport { get; } = transport;

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

    /// <summary>
    /// Broadcasts <paramref name="eventType"/> to every connection in <paramref name="sessionId"/>'s
    /// group, via <see cref="Transport"/> — the same group-scoped, typed path
    /// <see cref="Affiant.Core.Services.ReviewGate"/> uses for Evidence Cards. Both reference hosts
    /// independently converged on <c>BroadcastToGroupAsync(conversationId, ...)</c> as their
    /// session-broadcast shape (area-4 d1-fw-intent finding D); this is that path, first-class on
    /// the hub base instead of hand-rolled per host.
    /// </summary>
    protected Task BroadcastToSessionAsync(
        string sessionId, TransportEvent eventType, object payload, CancellationToken cancellationToken = default)
        => Transport.BroadcastToGroupAsync(GetSessionGroupName(sessionId), eventType, payload, cancellationToken);

    /// <summary>
    /// Broadcasts <paramref name="eventType"/> to every connection in <paramref name="reviewerId"/>'s
    /// reviewer group, via <see cref="Transport"/>. See <see cref="BroadcastToSessionAsync"/>'s remarks.
    /// </summary>
    protected Task BroadcastToReviewerAsync(
        string reviewerId, TransportEvent eventType, object payload, CancellationToken cancellationToken = default)
        => Transport.BroadcastToGroupAsync(GetReviewerGroupName(reviewerId), eventType, payload, cancellationToken);

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
