namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IRouteRegistry
{
    void Register(GuidableElement element);
    IReadOnlyList<GuidableElement> GetElementsForRoute(string route);
    IReadOnlyList<GuidableElement> GetAllElements();
    GuidableElement? GetElementById(string elementId);
}
