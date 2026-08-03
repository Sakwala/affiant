namespace Affiant.Transport.SignalR.Tests.UiBridge;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;
using Affiant.Core.UiBridge;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Area-4 P1f(b): real-SignalR proof that <see cref="UiGuidanceBridge.BroadcastGuidanceAsync"/>
/// delivers over the wire under the reference host's existing client method name, <c>"GuideUI"</c>
/// (see <see cref="TransportEvent.UiGuidance"/>'s docs) — the client keeps working unmodified across
/// the pin bump — with the exact pinned <see cref="UiGuidancePayload"/>/<see cref="UiGuidanceStep"/>
/// shape (<c>navigateTo</c>, <c>steps[].{elementId,title,description,prefillValue,side,highlightPadding}</c>,
/// <c>context</c>), camelCased per P1d's now-declared JSON protocol.
/// </summary>
[Collection("SignalR Transport")]
public sealed class UiGuidanceBridgeWireTests(TransportIntegrationTestFixture fixture)
{
    [Fact(DisplayName = "UiGuidanceBridge.BroadcastGuidanceAsync delivers via the existing \"GuideUI\" wire name with the pinned payload shape")]
    public async Task BroadcastGuidanceAsync_RealSignalR_DeliversAsGuideUI_WithPinnedShape()
    {
        const string sessionId = "ui-guidance-wire-session";
        var (client, _) = await fixture.CreateConnectedClientAsync(groupId: sessionId);
        await using var _disposeClient = client;

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<JsonElement>("GuideUI", payload => received.TrySetResult(payload));

        var transport = fixture.Server.Services.GetRequiredService<IStreamingTransport>();
        var routeRegistry = new StubRouteRegistry();
        var bridge = new UiGuidanceBridge(routeRegistry, transport);

        var payload = new UiGuidancePayload(
            NavigateTo: "/widgets/create",
            Steps:
            [
                bridge.BuildStep("name-input", "Enter the widget name.", prefillValue: "Bracket-42"),
            ],
            Context: "I'll guide you through creating a widget.");

        await bridge.BroadcastGuidanceAsync(sessionId, payload);

        var element = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("/widgets/create", element.GetProperty("navigateTo").GetString());
        Assert.Equal("I'll guide you through creating a widget.", element.GetProperty("context").GetString());

        var step = element.GetProperty("steps").EnumerateArray().Single();
        Assert.Equal("name-input", step.GetProperty("elementId").GetString());
        Assert.Equal("name-input", step.GetProperty("title").GetString()); // no registered displayName -> falls back to elementId
        Assert.Equal("Enter the widget name.", step.GetProperty("description").GetString());
        Assert.Equal("Bracket-42", step.GetProperty("prefillValue").GetString());
        Assert.Equal("bottom", step.GetProperty("side").GetString()); // documented default
        Assert.Equal(JsonValueKind.Null, step.GetProperty("highlightPadding").ValueKind);
    }

    private sealed class StubRouteRegistry : IRouteRegistry
    {
        public void Register(Abstractions.Models.GuidableElement element) { }
        public IReadOnlyList<Abstractions.Models.GuidableElement> GetElementsForRoute(string route) => [];
        public IReadOnlyList<Abstractions.Models.GuidableElement> GetAllElements() => [];
        public Abstractions.Models.GuidableElement? GetElementById(string elementId) => null;
    }
}
