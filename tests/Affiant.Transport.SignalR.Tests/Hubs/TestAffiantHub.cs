namespace Affiant.Transport.SignalR.Tests.Hubs;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;
using Affiant.Transport.SignalR.Hubs;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Minimal concrete hub for integration tests. Exposes helpers for group join and bidirectional
/// decision routing. Tests read their own connectionId client-side off
/// <see cref="Microsoft.AspNetCore.SignalR.Client.HubConnection.ConnectionId"/> after
/// <c>StartAsync()</c> — P4 (area-4) made <c>AffiantHub</c> a <c>Hub&lt;IAffiantHubClient&gt;</c>,
/// whose typed <c>Clients</c> proxy structurally cannot carry a test-only, non-<see cref="TransportEvent"/>
/// event like a former "ConnectionRegistered" announcement; the client-side property removes the
/// need for one entirely rather than adding a framework-vocabulary member for a test concern.
/// </summary>
public sealed class TestAffiantHub(
    IChatSessionStore chatSessionStore,
    IStreamingTransport transport) : AffiantHub(chatSessionStore, transport)
{
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
    /// P4 (area-4): calls the caller's own typed <see cref="IAffiantHubClient.ReceiveToken"/> method
    /// directly — the compile-time-checked shape a hub subclass's hot-path token streaming now has
    /// available, in place of the raw <c>Clients.Caller.SendAsync("ReceiveToken", chunk)</c> string
    /// literal both reference hosts used exclusively before this change.
    /// </summary>
    public Task StreamToken(string chunk) => Clients.Caller.ReceiveToken(chunk);

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
