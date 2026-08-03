namespace Affiant.Transport.SignalR.Tests.Hubs;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;
using Affiant.Transport.SignalR.Hubs;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Minimal concrete hub for integration tests. Broadcasts the connection ID on connect
/// so tests can target specific connections with SendAsync, and exposes helpers for
/// group join and bidirectional decision routing.
/// </summary>
public sealed class TestAffiantHub(
    IChatSessionStore chatSessionStore,
    IStreamingTransport transport) : AffiantHub(chatSessionStore, transport)
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        await Clients.Caller.SendAsync("ConnectionRegistered", Context.ConnectionId);
    }

    /// <summary>Adds the calling connection to a named SignalR group.</summary>
    public Task JoinGroup(string groupId)
        => Groups.AddToGroupAsync(Context.ConnectionId, groupId);

    /// <summary>
    /// Test-invokable wrapper over the protected, typed <c>BroadcastToSessionAsync</c> (P5c) — lets
    /// integration tests prove the hub base's own helper, not just the raw
    /// <see cref="IStreamingTransport.BroadcastToGroupAsync"/> call, delivers correctly.
    /// </summary>
    public Task BroadcastSession(string sessionId, TransportEvent eventType, object payload)
        => BroadcastToSessionAsync(sessionId, eventType, payload);

    /// <summary>Test-invokable wrapper over the protected, typed <c>BroadcastToReviewerAsync</c> (P5c).</summary>
    public Task BroadcastReviewer(string reviewerId, TransportEvent eventType, object payload)
        => BroadcastToReviewerAsync(reviewerId, eventType, payload);

    /// <summary>
    /// Routes a reviewer decision back to any live AwaitEvidenceCardResponseAsync waiter.
    /// Called by the test client to simulate the reviewer UI submitting an approval.
    /// </summary>
    public Task SubmitDecision(Guid docketId, bool approved)
    {
        Transport.TryDeliverResponse(docketId, new EvidenceCardResponse(
            docketId,
            approved ? ApprovalDecision.Approved : ApprovalDecision.Rejected));
        return Task.CompletedTask;
    }
}
