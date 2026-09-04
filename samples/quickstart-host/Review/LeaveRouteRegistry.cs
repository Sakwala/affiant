namespace QuickstartHost.Review;

using System.Collections.Concurrent;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// The host's map of elements the framework may point a user at, by semantic id — never by CSS
/// selector. Rule 6: the framework knows an element exists because the UI layer registered it,
/// not because something inspected the DOM.
///
/// <para>
/// <b>Why a sample this small has one at all.</b> <c>AddAffiantCore</c> registers the framework's
/// guidance bridge as a singleton, and that bridge takes an <c>IRouteRegistry</c>. ASP.NET Core
/// validates every singleton at build time in the Development environment, so a host that
/// registers no route registry does not start in Development — it throws before the first
/// request, naming this interface. Registering a real one, however small, is the answer; the
/// alternative is running the host outside Development, which is not an answer.
/// </para>
/// </summary>
public sealed class LeaveRouteRegistry : IRouteRegistry
{
    private readonly ConcurrentDictionary<string, GuidableElement> _elements = new(StringComparer.Ordinal);

    public LeaveRouteRegistry()
    {
        Register(new GuidableElement("chat-input", "textarea",
            new Dictionary<string, object> { ["route"] = "/" }));
        Register(new GuidableElement("evidence-card", "region",
            new Dictionary<string, object> { ["route"] = "/" }));
    }

    public void Register(GuidableElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        _elements[element.ElementId] = element;
    }

    public IReadOnlyList<GuidableElement> GetElementsForRoute(string route) =>
        _elements.Values
            .Where(e => e.Attributes.TryGetValue("route", out var value) && Equals(value, route))
            .ToArray();

    public IReadOnlyList<GuidableElement> GetAllElements() => _elements.Values.ToArray();

    public GuidableElement? GetElementById(string elementId) =>
        _elements.TryGetValue(elementId, out var element) ? element : null;
}
