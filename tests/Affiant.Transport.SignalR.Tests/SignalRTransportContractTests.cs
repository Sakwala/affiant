namespace Affiant.Transport.SignalR.Tests;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Validates that every <see cref="TransportEvent"/> value flows correctly through the
/// SignalR transport from <see cref="IStreamingTransport"/> to a connected HubConnection client.
/// </summary>
[Collection("SignalR Transport")]
public sealed class SignalRTransportContractTests(TransportIntegrationTestFixture fixture)
{
    // Client method names are defined by TransportEventExtensions.ToClientEventName()
    // (internal to the transport assembly). These constants document the client contract.
    private const string ConfirmActionMethod    = "ConfirmAction";
    private const string EvidenceCardRespMethod = "EvidenceCardResponse";
    private const string ReceiveTokenMethod     = "ReceiveToken";
    private const string UserMessageMethod      = "UserMessage";
    private const string ContextUpdatedMethod   = "ContextUpdated";
    private const string SystemNotifMethod      = "SystemNotification";

    [Fact(DisplayName = "Round-trip EvidenceCardRequest → ConfirmAction")]
    public async Task RoundTrip_EvidenceCardRequest()
    {
        var (client, connId) = await fixture.CreateConnectedClientAsync();
        await using var _ = client;

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>(ConfirmActionMethod, payload => received.TrySetResult(payload));

        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        await transport.SendAsync(connId, TransportEvent.EvidenceCardRequest,
            new { docketId = Guid.NewGuid() }, CancellationToken.None);

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
    }

    [Fact(DisplayName = "Round-trip EvidenceCardResponse")]
    public async Task RoundTrip_EvidenceCardResponse()
    {
        var (client, connId) = await fixture.CreateConnectedClientAsync();
        await using var _ = client;

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>(EvidenceCardRespMethod, payload => received.TrySetResult(payload));

        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        await transport.SendAsync(connId, TransportEvent.EvidenceCardResponse,
            new { docketId = Guid.NewGuid(), decision = "Approved" }, CancellationToken.None);

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
    }

    [Fact(DisplayName = "Round-trip AgentMessage → ReceiveToken")]
    public async Task RoundTrip_AgentMessage()
    {
        var (client, connId) = await fixture.CreateConnectedClientAsync();
        await using var _ = client;

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>(ReceiveTokenMethod, payload => received.TrySetResult(payload));

        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        await transport.SendAsync(connId, TransportEvent.AgentMessage,
            new { token = "Hello from agent" }, CancellationToken.None);

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
    }

    [Fact(DisplayName = "Round-trip UserMessage")]
    public async Task RoundTrip_UserMessage()
    {
        var (client, connId) = await fixture.CreateConnectedClientAsync();
        await using var _ = client;

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>(UserMessageMethod, payload => received.TrySetResult(payload));

        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        await transport.SendAsync(connId, TransportEvent.UserMessage,
            new { content = "User said hello" }, CancellationToken.None);

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
    }

    [Fact(DisplayName = "Round-trip ContextUpdate → ContextUpdated")]
    public async Task RoundTrip_ContextUpdate()
    {
        var (client, connId) = await fixture.CreateConnectedClientAsync();
        await using var _ = client;

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>(ContextUpdatedMethod, payload => received.TrySetResult(payload));

        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        await transport.SendAsync(connId, TransportEvent.ContextUpdate,
            new { entity = "Aircraft", id = "AC-001" }, CancellationToken.None);

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
    }

    [Fact(DisplayName = "Round-trip SystemNotification")]
    public async Task RoundTrip_SystemNotification()
    {
        var (client, connId) = await fixture.CreateConnectedClientAsync();
        await using var _ = client;

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>(SystemNotifMethod, payload => received.TrySetResult(payload));

        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        await transport.SendAsync(connId, TransportEvent.SystemNotification,
            new { level = "warning", message = "Session expiring" }, CancellationToken.None);

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
    }

    [Fact(DisplayName = "Payload round-trips as symmetric JSON")]
    public async Task Payload_SerializationIsSymmetric()
    {
        var (client, connId) = await fixture.CreateConnectedClientAsync();
        await using var _ = client;

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>(ReceiveTokenMethod, payload => received.TrySetResult(payload));

        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        await transport.SendAsync(connId, TransportEvent.AgentMessage,
            new { name = "Test", value = 42, nested = new { key = "val" } }, CancellationToken.None);

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Test", element.GetProperty("name").GetString());
        Assert.Equal(42, element.GetProperty("value").GetInt32());
        Assert.Equal("val", element.GetProperty("nested").GetProperty("key").GetString());
    }

    [Fact(DisplayName = "BroadcastToGroupAsync delivers to group members")]
    public async Task BroadcastToGroup_DeliversToGroupMembers()
    {
        const string testGroup = "broadcast-test-group";
        var (client, _) = await fixture.CreateConnectedClientAsync(groupId: testGroup);
        await using var __ = client;

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>(ReceiveTokenMethod, payload => received.TrySetResult(payload));

        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        await transport.BroadcastToGroupAsync(testGroup, TransportEvent.AgentMessage,
            new { token = "broadcast" }, CancellationToken.None);

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
    }
}
