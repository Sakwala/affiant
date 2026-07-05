namespace Affiant.AgentFramework.Extensions;

/// <summary>
/// Options for <see cref="ServiceCollectionExtensions.AddAffiantAgentFramework"/>.
/// </summary>
public sealed class AgentFrameworkOptions
{
    /// <summary>
    /// Names of hosted/provider-side tools (e.g. <c>"code_interpreter"</c>, <c>"web_search_preview"</c>)
    /// that the host explicitly acknowledges Affiant cannot see, tag, or gate — because MAF's
    /// function-calling middleware only fires for client-invoked <c>AIFunction</c> tools (framework
    /// spec / proposal §4.6). Any hosted tool not named here causes <c>WithAffiant</c> to throw.
    /// Default: empty (every hosted tool is refused).
    /// </summary>
    public IReadOnlyList<string> AcknowledgeUncoveredTools { get; set; } = [];
}
