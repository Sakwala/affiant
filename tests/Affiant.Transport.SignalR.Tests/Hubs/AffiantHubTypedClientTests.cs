namespace Affiant.Transport.SignalR.Tests.Hubs;

using System.Reflection;
using System.Text.Json;
using Affiant.Abstractions.Transport;
using Affiant.Transport.SignalR.Hubs;
using Affiant.Transport.SignalR.Transport;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

/// <summary>
/// P4 (area-4, ruled 2026-08-04): <c>AffiantHub</c> is now <c>Hub&lt;IAffiantHubClient&gt;</c> — a
/// hub subclass's own <c>Clients.Caller</c>/<c>Clients.Group(...)</c> calls are compile-time checked
/// against <see cref="IAffiantHubClient"/> instead of taking a raw string method name. Two proofs:
/// (1) the typed call path (<see cref="TestAffiantHub.StreamToken"/>, calling
/// <c>Clients.Caller.ReceiveToken(chunk)</c> directly) still delivers over real SignalR under the
/// exact wire name <see cref="TransportEventExtensions.ToClientEventName"/> would have produced for
/// <see cref="TransportEvent.AgentMessage"/>; (2) <see cref="IAffiantHubClient"/>'s method set is
/// exactly (not a subset, not a superset of) the pinned wire-name set every
/// <see cref="TransportEvent"/> member maps to — locking the two together so one can't silently
/// drift from the other.
/// </summary>
[Collection("SignalR Transport")]
public sealed class AffiantHubTypedClientTests(TransportIntegrationTestFixture fixture)
{
    [Fact(DisplayName = "Typed Clients.Caller.ReceiveToken(...) call delivers over real SignalR under the \"ReceiveToken\" wire name")]
    public async Task TypedClientCall_RealSignalR_DeliversUnderPinnedWireName()
    {
        var (client, _) = await fixture.CreateConnectedClientAsync();
        await using var _disposeClient = client;

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>("ReceiveToken", payload => received.TrySetResult(payload));

        await client.InvokeAsync("StreamToken", "hello-from-typed-client");

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello-from-typed-client", element.GetString());
    }

    [Fact]
    public void IAffiantHubClient_MethodNames_ExactlyMatch_ToClientEventName_OutputsForEveryTransportEventMember()
    {
        var interfaceMethodNames = typeof(IAffiantHubClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var expectedWireNames = Enum.GetValues<TransportEvent>()
            .Select(evt => evt.ToClientEventName())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expectedWireNames, interfaceMethodNames);
    }
}
