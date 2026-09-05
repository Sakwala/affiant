namespace Affiant.Transport.SignalR.Tests.Hubs;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;
using Affiant.Transport.SignalR.Hubs;
using Affiant.Transport.SignalR.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

/// <summary>
/// P5c (area-4 d1-fw-intent finding D / d1-host-bypass finding D): AffiantHub's own
/// BroadcastToSessionAsync/BroadcastToReviewerAsync helpers used to take a raw string method name
/// and call Clients.Group(...).SendAsync(...) directly, bypassing the framework's own
/// TransportEvent/IStreamingTransport abstraction — the strongest evidence in the whole area-4
/// investigation that the bypass was a shape gap, not host laziness. These tests prove the fix:
/// (1) unit-level — the helpers now genuinely route through the injected IStreamingTransport with
/// the right group name and TransportEvent; (2) integration-level — the real SignalR wire delivery
/// still works end to end and uses ToClientEventName()'s mapping, exactly like every other
/// IStreamingTransport-mediated send.
/// </summary>
[Collection("SignalR Transport")]
public sealed class AffiantHubBroadcastHelperTests(TransportIntegrationTestFixture fixture)
{
    [Fact]
    public async Task BroadcastToSessionAsync_RoutesThrough_InjectedTransport_WithSessionGroupName_Unit()
    {
        var spy = new SpyStreamingTransport();
        var hub = new ProbeHub(new NullChatSessionStore(), spy);

        var payload = new { message = "hello" };
        await hub.CallBroadcastSession("session-42", TransportEvent.SystemNotification, payload);

        var call = Assert.Single(spy.GroupCalls);
        Assert.Equal("session-42", call.GroupId); // GetSessionGroupName default: verbatim
        Assert.Equal(TransportEvent.SystemNotification, call.EventType);
        Assert.Same(payload, call.Payload);
    }

    [Fact]
    public async Task BroadcastToReviewerAsync_RoutesThrough_InjectedTransport_WithReviewerGroupName()
    {
        var spy = new SpyStreamingTransport();
        var hub = new ProbeHub(new NullChatSessionStore(), spy);

        var payload = new { docketId = Guid.NewGuid() };
        await hub.CallBroadcastReviewer("reviewer-7", TransportEvent.EvidenceCardRequest, payload);

        var call = Assert.Single(spy.GroupCalls);
        Assert.Equal("reviewer:reviewer-7", call.GroupId); // GetReviewerGroupName's fixed prefix
        Assert.Equal(TransportEvent.EvidenceCardRequest, call.EventType);
        Assert.Same(payload, call.Payload);
    }

    [Fact(DisplayName = "Real SignalR round trip: AffiantHub.BroadcastToSessionAsync delivers via ToClientEventName mapping")]
    public async Task BroadcastToSessionAsync_RealSignalR_DeliversToSessionGroup()
    {
        const string sessionId = "hub-helper-session";
        var (client, _) = await fixture.CreateConnectedClientAsync(groupId: sessionId);
        await using var __ = client;

        // ContextUpdate -> "ContextUpdated" per ToClientEventName(); distinct from the raw enum
        // name so this also proves the typed helper goes through the real mapping, not evt.ToString().
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>("ContextUpdated", payload => received.TrySetResult(payload));

        var (broadcaster, _) = await fixture.CreateConnectedClientAsync();
        await using var ___ = broadcaster;
        await broadcaster.InvokeAsync(
            "BroadcastSession", sessionId, TransportEvent.ContextUpdate, new { summary = "updated" });

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("updated", element.GetProperty("summary").GetString());
    }

    [Fact(DisplayName = "Real SignalR round trip: AffiantHub.BroadcastToReviewerAsync targets the reviewer:{id} group, not the raw id")]
    public async Task BroadcastToReviewerAsync_RealSignalR_DeliversOnlyToReviewerGroup_NotBareId()
    {
        const string reviewerId = "hub-helper-reviewer";

        // Joins the LITERAL id as a group — must NOT receive the reviewer broadcast, proving the
        // helper applies GetReviewerGroupName's "reviewer:" prefix rather than the bare id.
        var (bareIdClient, _) = await fixture.CreateConnectedClientAsync(groupId: reviewerId);
        await using var __ = bareIdClient;
        var bareIdReceived = false;
        bareIdClient.On<JsonElement>("ConfirmAction", _ => bareIdReceived = true);

        var (reviewerClient, _) = await fixture.CreateConnectedClientAsync(groupId: $"reviewer:{reviewerId}");
        await using var ___ = reviewerClient;
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        reviewerClient.On<JsonElement>("ConfirmAction", payload => received.TrySetResult(payload));

        var (broadcaster, _) = await fixture.CreateConnectedClientAsync();
        await using var ____ = broadcaster;
        await broadcaster.InvokeAsync(
            "BroadcastReviewer", reviewerId, TransportEvent.EvidenceCardRequest, new { docketId = "d-1" });

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("d-1", element.GetProperty("docketId").GetString());
        Assert.False(bareIdReceived);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Thin subclass exposing AffiantHub's protected broadcast helpers under test without a live
    /// SignalR connection — the base class's `protected` members are directly accessible here.
    /// </summary>
    private sealed class ProbeHub(IChatSessionStore chatSessionStore, IStreamingTransport transport)
        : AffiantHub(chatSessionStore, transport)
    {
        // Both members are `protected` on AffiantHub — directly callable from this subclass, no
        // reflection needed. These public wrappers just give the test call sites above a name.
        public Task CallBroadcastSession(string sessionId, TransportEvent eventType, object payload) =>
            BroadcastToSessionAsync(sessionId, eventType, payload);

        public Task CallBroadcastReviewer(string reviewerId, TransportEvent eventType, object payload) =>
            BroadcastToReviewerAsync(reviewerId, eventType, payload);
    }

    private sealed class SpyStreamingTransport : IStreamingTransport
    {
        public List<(string GroupId, TransportEvent EventType, object Payload)> GroupCalls { get; } = [];

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct) =>
            Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            GroupCalls.Add((groupId, eventType, payload));
            return Task.CompletedTask;
        }

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default) =>
            Task.FromCanceled<DecisionHandOff>(ct);
    }
}
