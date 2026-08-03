namespace Affiant.Transport.SignalR.Tests.Extensions;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;
using Affiant.Transport.SignalR.Extensions;
using Affiant.Transport.SignalR.Tests.Hubs;
using Affiant.Transport.SignalR.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// P1d (area-4, ruled 2026-08-04): <c>AddAffiantSignalR</c> now explicitly configures the hub JSON
/// protocol instead of relying on ASP.NET Core's ambient <c>JsonHubProtocol</c> default. These tests
/// assert the policy FROM THE CONFIGURED OPTIONS directly — the actual requirement — rather than
/// only observing it work through a live round trip, which would conflate "we configured this" with
/// "it happens to work today via an unrelated ambient default" (exactly the gap V1 flagged: before
/// this change, camelCase was real but asserted nowhere in framework source, only in a test comment
/// pointing at ASP.NET Core's own default).
/// </summary>
public class SignalRJsonProtocolConfigurationTests
{
    [Fact]
    public void AddAffiantSignalR_ConfiguresJsonHubProtocol_CamelCase_FromOptions_NotAmbientDefaults()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatSessionStore, NullChatSessionStore>();
        services.AddAffiantSignalR<TestAffiantHub>();

        var sp = services.BuildServiceProvider();
        var jsonOptions = sp.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;

        Assert.Same(JsonNamingPolicy.CamelCase, jsonOptions.PayloadSerializerOptions.PropertyNamingPolicy);
    }

    [Fact]
    public void AddAffiantSignalR_ConfiguresJsonHubProtocol_StringEnumConverter_FromOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatSessionStore, NullChatSessionStore>();
        services.AddAffiantSignalR<TestAffiantHub>();

        var sp = services.BuildServiceProvider();
        var jsonOptions = sp.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;

        Assert.Contains(
            jsonOptions.PayloadSerializerOptions.Converters,
            c => c is System.Text.Json.Serialization.JsonStringEnumConverter);
    }

    [Fact(DisplayName = "P1d wire-visible change: ApprovalDecision now crosses as a STRING (was an int) — matches ProvenanceSource's treatment")]
    public async Task ApprovalDecision_CrossesTheWire_AsAString_NotAnInt()
    {
        var fixture = new TransportIntegrationTestFixture();
        await fixture.InitializeAsync();
        try
        {
            var (client, connId) = await fixture.CreateConnectedClientAsync();
            await using var _ = client;

            var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.On<JsonElement>("EvidenceCardResponse", payload => received.TrySetResult(payload));

            var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
            var response = new EvidenceCardResponse(Guid.NewGuid(), ApprovalDecision.Approved);
            await transport.SendAsync(connId, TransportEvent.EvidenceCardResponse, response, CancellationToken.None);

            // 30s, not 5s: under full-solution parallel test load the real Kestrel/SignalR round
            // trip flaked once at 5s (refuter lens 6, 2026-08-04); the timeout only bounds hangs.
            var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var decision = element.GetProperty("decision");
            Assert.Equal(JsonValueKind.String, decision.ValueKind);
            Assert.Equal("Approved", decision.GetString());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
