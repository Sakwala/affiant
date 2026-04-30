using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

namespace Affiant.Core.UiBridge;

/// <summary>
/// Surfaces registered <see cref="GuidableElement"/> entries from <see cref="IRouteRegistry"/>
/// to host UI layers. Acts as the framework-side half of the data-guide contract (normative rule 6):
/// the host registers UI elements via <c>IRouteRegistry</c>; the bridge exposes them for
/// downstream consumers (hubs, SignalR transports, etc.) without coupling to DOM or CSS selectors.
/// </summary>
public class UiGuidanceBridge(IRouteRegistry routeRegistry)
{
    public IReadOnlyList<GuidableElement> GetGuidableElements() =>
        routeRegistry.GetAllElements();

    public IReadOnlyList<GuidableElement> GetGuidableElementsForRoute(string route) =>
        routeRegistry.GetElementsForRoute(route);

    public GuidableElement? GetGuidableElementById(string elementId) =>
        routeRegistry.GetElementById(elementId);
}
