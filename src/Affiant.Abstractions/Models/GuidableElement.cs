namespace Affiant.Abstractions.Models;

/// <summary>
/// Immutable record representing a UI element that can receive guidance from the framework.
/// Used by <c>IRouteRegistry</c> to communicate UI state changes (visibility, enablement, etc.)
/// from the framework to the host's UI layer. See framework spec §4.3.
/// </summary>
public record GuidableElement(
    string ElementId,
    string ElementType,
    Dictionary<string, object>? Attributes = null)
{
    public Dictionary<string, object> Attributes { get; } = Attributes ?? new Dictionary<string, object>();
}
