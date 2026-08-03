using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;

namespace Affiant.Core.UiBridge;

/// <summary>
/// Surfaces registered <see cref="GuidableElement"/> entries from <see cref="IRouteRegistry"/>
/// to host UI layers, and assembles/broadcasts <see cref="UiGuidancePayload"/> walkthroughs through
/// <see cref="IStreamingTransport"/> (<see cref="TransportEvent.UiGuidance"/>, wire method
/// <c>"GuideUI"</c>). Acts as the framework-side half of the data-guide contract (normative rule 6,
/// built 2026-08-04 per the area-4 Decision-1 ruling): the host registers UI elements via
/// <c>IRouteRegistry</c>; this bridge exposes them and now carries the wire path too, instead of a
/// host hand-rolling <c>Clients.Group(...).SendAsync("GuideUI", ...)</c> directly (the exact bypass
/// pattern documented as host folklore before this change — see the area-4 fw-wire-census pack).
///
/// <para>
/// <b>What stays host-owned:</b> per-step content — which fields to guide through, prefill values
/// derived from conversation state, and description text chosen by field provenance — is inherently
/// domain-specific (a "work order form" vs. a "leave request form" have nothing in common at this
/// layer) and is composed by the host's own guidance tool, then passed to <see cref="BuildStep"/> /
/// <see cref="BroadcastGuidanceAsync"/>. This mirrors <see cref="Services.ReviewGate"/>'s split:
/// the framework carries the mechanism (filing, broadcasting), the host supplies the content
/// (<c>IReviewContextProvider</c> there; step text/prefill values here).
/// </para>
/// </summary>
public class UiGuidanceBridge(IRouteRegistry routeRegistry, IStreamingTransport transport)
{
    public IReadOnlyList<GuidableElement> GetGuidableElements() =>
        routeRegistry.GetAllElements();

    public IReadOnlyList<GuidableElement> GetGuidableElementsForRoute(string route) =>
        routeRegistry.GetElementsForRoute(route);

    public GuidableElement? GetGuidableElementById(string elementId) =>
        routeRegistry.GetElementById(elementId);

    /// <summary>
    /// Builds one <see cref="UiGuidanceStep"/> for <paramref name="elementId"/>, resolving its
    /// popover placement (<c>side</c>) and highlight padding from the <see cref="GuidableElement"/>
    /// registered for it — the same <c>Attributes</c>-bag convention (<c>"side"</c>,
    /// <c>"highlightPadding"</c>, <c>"displayName"</c>) a reference host's guidance tool already used
    /// ad hoc, now centralized here so every host gets the same fallback behavior
    /// (<c>side</c> defaults to <c>"bottom"</c>; no highlight-padding default is asserted — the
    /// client renderer's own default applies when <see cref="UiGuidanceStep.HighlightPadding"/> is
    /// <see langword="null"/>) instead of re-deriving it per host. Falls back to
    /// <paramref name="elementId"/> itself for <see cref="UiGuidanceStep.Title"/> when neither
    /// <paramref name="title"/> nor a registered <c>"displayName"</c> attribute is available, and
    /// never throws when <paramref name="elementId"/> is not registered — a guidance tour should
    /// degrade to defaults, not fail the turn, over one unregistered element.
    /// </summary>
    /// <param name="elementId">The <c>IRouteRegistry</c> semantic element id.</param>
    /// <param name="description">Popover body text — host-composed.</param>
    /// <param name="prefillValue">Value the client should pre-fill for this element, if any.</param>
    /// <param name="title">
    /// Explicit title override. When omitted, falls back to the registered element's
    /// <c>"displayName"</c> attribute, then to <paramref name="elementId"/> itself.
    /// </param>
    public UiGuidanceStep BuildStep(
        string elementId,
        string description,
        string? prefillValue = null,
        string? title = null)
    {
        var element = routeRegistry.GetElementById(elementId);

        var resolvedTitle = title
            ?? element?.Attributes.GetValueOrDefault("displayName") as string
            ?? elementId;
        var side = element?.Attributes.GetValueOrDefault("side") as string ?? "bottom";
        var highlightPadding = element?.Attributes.GetValueOrDefault("highlightPadding") is int padding
            ? padding
            : (int?)null;

        return new UiGuidanceStep(elementId, resolvedTitle, description, prefillValue, side, highlightPadding);
    }

    /// <summary>
    /// Broadcasts a fully-assembled <see cref="UiGuidancePayload"/> to <paramref name="sessionGroupId"/>
    /// via <see cref="TransportEvent.UiGuidance"/> (wire method <c>"GuideUI"</c>). The framework-owned
    /// wire path Rule 6 previously lacked entirely — see this class's own remarks.
    /// </summary>
    public Task BroadcastGuidanceAsync(
        string sessionGroupId, UiGuidancePayload payload, CancellationToken cancellationToken = default) =>
        transport.BroadcastToGroupAsync(sessionGroupId, TransportEvent.UiGuidance, payload, cancellationToken);
}
