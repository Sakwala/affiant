namespace Affiant.Core.Tests.UiBridge;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.UiBridge;
using Xunit;

/// <summary>
/// Area-4 P1f(b) (Rule 6 built 2026-08-04): <see cref="UiGuidanceBridge"/> is now the framework-owned
/// wire path for UI guidance walkthroughs — <see cref="UiGuidanceBridge.BuildStep"/> assembles a
/// <see cref="UiGuidanceStep"/> from a registered <see cref="GuidableElement"/>'s attributes (the
/// same convention a reference host's guidance tool used ad hoc), and
/// <see cref="UiGuidanceBridge.BroadcastGuidanceAsync"/> sends the assembled
/// <see cref="UiGuidancePayload"/> through <see cref="IStreamingTransport"/> as
/// <see cref="TransportEvent.UiGuidance"/>.
/// </summary>
public class UiGuidanceBridgeTests
{
    [Fact]
    public void BuildStep_RegisteredElement_ResolvesSideAndHighlightPaddingAndDisplayNameFromAttributes()
    {
        var registry = new FakeRouteRegistry();
        registry.Register(new GuidableElement(
            "title-input",
            "input",
            new Dictionary<string, object>
            {
                ["displayName"] = "Title",
                ["side"] = "right",
                ["highlightPadding"] = 12,
            }));
        var bridge = new UiGuidanceBridge(registry, new SpyTransport());

        var step = bridge.BuildStep("title-input", "Enter a title.", prefillValue: "Landing gear inspection");

        Assert.Equal("title-input", step.ElementId);
        Assert.Equal("Title", step.Title);
        Assert.Equal("Enter a title.", step.Description);
        Assert.Equal("Landing gear inspection", step.PrefillValue);
        Assert.Equal("right", step.Side);
        Assert.Equal(12, step.HighlightPadding);
    }

    [Fact]
    public void BuildStep_UnregisteredElement_DegradesToDefaults_DoesNotThrow()
    {
        var registry = new FakeRouteRegistry();
        var bridge = new UiGuidanceBridge(registry, new SpyTransport());

        var step = bridge.BuildStep("unregistered-element", "Some description.");

        Assert.Equal("unregistered-element", step.Title); // falls back to elementId
        Assert.Equal("bottom", step.Side);                // documented default
        Assert.Null(step.HighlightPadding);                // no default asserted — client renderer's own default applies
        Assert.Null(step.PrefillValue);
    }

    [Fact]
    public void BuildStep_ExplicitTitleOverride_WinsOverRegisteredDisplayName()
    {
        var registry = new FakeRouteRegistry();
        registry.Register(new GuidableElement(
            "aircraft-select", "select",
            new Dictionary<string, object> { ["displayName"] = "Aircraft" }));
        var bridge = new UiGuidanceBridge(registry, new SpyTransport());

        var step = bridge.BuildStep("aircraft-select", "Pick one.", title: "Choose Aircraft");

        Assert.Equal("Choose Aircraft", step.Title);
    }

    [Fact]
    public async Task BroadcastGuidanceAsync_SendsPinnedPayloadShape_Through_UiGuidanceTransportEvent()
    {
        var registry = new FakeRouteRegistry();
        var transport = new SpyTransport();
        var bridge = new UiGuidanceBridge(registry, transport);

        var payload = new UiGuidancePayload(
            NavigateTo: "/create",
            Steps:
            [
                new UiGuidanceStep("field-a", "Field A", "Fill this in.", "prefilled", "bottom", 8),
            ],
            Context: "Walkthrough started.");

        await bridge.BroadcastGuidanceAsync("session-42", payload);

        var call = Assert.Single(transport.Broadcasts);
        Assert.Equal("session-42", call.GroupId);
        Assert.Equal(TransportEvent.UiGuidance, call.EventType);
        Assert.Same(payload, call.Payload);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class FakeRouteRegistry : IRouteRegistry
    {
        private readonly Dictionary<string, GuidableElement> _elements = [];

        public void Register(GuidableElement element) => _elements[element.ElementId] = element;
        public IReadOnlyList<GuidableElement> GetElementsForRoute(string route) => [.. _elements.Values];
        public IReadOnlyList<GuidableElement> GetAllElements() => [.. _elements.Values];
        public GuidableElement? GetElementById(string elementId) =>
            _elements.TryGetValue(elementId, out var e) ? e : null;
    }

    private sealed class SpyTransport : IStreamingTransport
    {
        public List<(string GroupId, TransportEvent EventType, object Payload)> Broadcasts { get; } = [];

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct) =>
            Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            Broadcasts.Add((groupId, eventType, payload));
            return Task.CompletedTask;
        }

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default) =>
            Task.FromCanceled<DecisionHandOff>(ct);
    }
}
