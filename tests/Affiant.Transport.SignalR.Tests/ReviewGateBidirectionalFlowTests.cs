namespace Affiant.Transport.SignalR.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Validates the bidirectional ReviewGate ↔ client handshake at the transport level:
/// AwaitEvidenceCardResponseAsync blocks until TryDeliverResponse is called, either directly or via
/// a hub method invoked by the client.
/// </summary>
[Collection("SignalR Transport")]
public sealed class ReviewGateBidirectionalFlowTests(TransportIntegrationTestFixture fixture)
{
    [Fact(DisplayName = "TryDeliverResponse unblocks AwaitEvidenceCardResponseAsync")]
    public async Task TryDeliverResponse_UnblocksAwaitEvidenceCardResponseAsync()
    {
        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        var docketId = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start awaiting before the response arrives
        var awaitTask = transport.AwaitEvidenceCardResponseAsync("group-a", docketId, cts.Token);

        // Ensure the TCS is registered before delivering
        await Task.Delay(20, cts.Token);

        var delivered = transport.TryDeliverResponse(
            docketId, new EvidenceCardResponse(docketId, ApprovalDecision.Approved));

        Assert.True(delivered, "TryDeliverResponse should find the live waiter");

        var result = await awaitTask;
        Assert.Equal(ApprovalDecision.Approved, result.Decision);
        Assert.Equal(docketId, result.DocketId);
    }

    [Fact(DisplayName = "TryDeliverResponse returns false when no waiter exists")]
    public async Task TryDeliverResponse_ReturnsFalse_WhenNoWaiter()
    {
        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();

        var delivered = transport.TryDeliverResponse(
            Guid.NewGuid(), new EvidenceCardResponse(Guid.NewGuid(), ApprovalDecision.Rejected));

        Assert.False(delivered);
        await Task.CompletedTask;
    }

    [Fact(DisplayName = "Hub SubmitDecision method routes approval back to AwaitEvidenceCardResponseAsync")]
    public async Task HubSubmitDecision_RoutesApproval_ToAwaitEvidenceCardResponseAsync()
    {
        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        var docketId = Guid.NewGuid();
        const string reviewGroup = "reviewer-hub-test";

        var (client, _) = await fixture.CreateConnectedClientAsync(groupId: reviewGroup);
        await using var _ = client;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 1. Start awaiting the reviewer response
        var awaitTask = transport.AwaitEvidenceCardResponseAsync(reviewGroup, docketId, cts.Token);

        // 2. Client receives the EvidenceCardRequest and submits a decision via the hub method
        var requestReceived = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        client.On<System.Text.Json.JsonElement>("ConfirmAction", msg =>
        {
            // Fire-and-forget: invoke the hub method to route the decision back.
            // Assigning to a named variable and passing to GC.KeepAlive avoids
            // both CS4014 (unawaited task) and CS0219 (unused variable).
            var pendingDecision = Task.Run(async () =>
            {
                await client.InvokeAsync("SubmitDecision", docketId, true, cts.Token);
                requestReceived.TrySetResult(true);
            }, cts.Token);
            GC.KeepAlive(pendingDecision);
        });

        // 3. Broadcast the EvidenceCardRequest to the reviewer group
        await transport.BroadcastToGroupAsync(
            reviewGroup, TransportEvent.EvidenceCardRequest,
            new { docketId }, cts.Token);

        // 4. Wait for the client to invoke SubmitDecision
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 5. AwaitEvidenceCardResponseAsync should now be unblocked
        var response = await awaitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ApprovalDecision.Approved, response.Decision);
        Assert.Equal(docketId, response.DocketId);
    }

    [Fact(DisplayName = "Multiple concurrent groups receive isolated events")]
    public async Task ConcurrentGroups_ReceiveIsolatedEvents()
    {
        const string group1 = "concurrent-group-1";
        const string group2 = "concurrent-group-2";

        var (client1, _) = await fixture.CreateConnectedClientAsync(groupId: group1);
        await using var _1 = client1;
        var (client2, _) = await fixture.CreateConnectedClientAsync(groupId: group2);
        await using var _2 = client2;

        var g1Received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var g2Received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        client1.On<System.Text.Json.JsonElement>("ReceiveToken", _ => g1Received.TrySetResult(true));
        client2.On<System.Text.Json.JsonElement>("ReceiveToken", _ => g2Received.TrySetResult(true));

        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();

        await Task.WhenAll(
            transport.BroadcastToGroupAsync(group1, TransportEvent.AgentMessage,
                new { group = "group1" }, CancellationToken.None),
            transport.BroadcastToGroupAsync(group2, TransportEvent.AgentMessage,
                new { group = "group2" }, CancellationToken.None));

        var r1 = await g1Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var r2 = await g2Received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(r1);
        Assert.True(r2);
    }
}
