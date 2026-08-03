namespace Affiant.Abstractions.Transport;

/// <summary>
/// One step of a UI guidance walkthrough — a single highlighted, described, optionally
/// pre-filled element the client renderer resolves via its own <c>data-guide</c>-attribute lookup
/// (framework spec Rule 6: the framework references elements only by their <c>IRouteRegistry</c>
/// semantic <see cref="Models.GuidableElement.ElementId"/>, never a CSS selector).
/// </summary>
/// <param name="ElementId">
/// The <see cref="Models.GuidableElement.ElementId"/> registered via <c>IRouteRegistry</c>. The only
/// thing that ties this step to a real DOM element — resolved client-side, never a selector.
/// </param>
/// <param name="Title">Popover title shown for this step.</param>
/// <param name="Description">Popover body text — host-composed, may vary by field provenance.</param>
/// <param name="PrefillValue">
/// Value the client should pre-fill into this element before showing the popover, if any.
/// </param>
/// <param name="Side">Popover placement relative to the element: <c>"top"</c>, <c>"bottom"</c>,
/// <c>"left"</c>, or <c>"right"</c>.</param>
/// <param name="HighlightPadding">Highlight-box padding in pixels around the element, if the client
/// renderer supports it.</param>
public sealed record UiGuidanceStep(
    string ElementId,
    string Title,
    string Description,
    string? PrefillValue,
    string Side,
    int? HighlightPadding);

/// <summary>
/// Payload sent to the UI to start a guidance walkthrough. Transported via
/// <see cref="TransportEvent.UiGuidance"/> (wire method name <c>"GuideUI"</c>).
///
/// <b>Pinned from the reference host's existing client contract (area-4 P1f(b), 2026-08-04):</b>
/// this shape — including property names, once camelCased by the hub JSON protocol (P1d) — is not a
/// framework invention; it mirrors the walkthrough payload a reference host's own guidance tool
/// already emits and its own client renderer already consumes, pinned read-only from that host's
/// checked-in source at framework main <c>fc46b95</c>. Recording it here as the framework's own
/// typed contract is what makes Rule 6's mechanism real instead of host folklore — see
/// <see cref="Affiant.Core.UiBridge.UiGuidanceBridge"/> for the framework-owned assembly/broadcast
/// half.
/// </summary>
/// <param name="NavigateTo">Route the client should navigate to before starting the walkthrough.</param>
/// <param name="Steps">Ordered walkthrough steps.</param>
/// <param name="Context">Short summary text shown alongside the walkthrough (e.g. what was pre-filled and why).</param>
public sealed record UiGuidancePayload(
    string NavigateTo,
    IReadOnlyList<UiGuidanceStep> Steps,
    string Context);
