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

    /// <summary>
    /// Explicit host acknowledgment that <c>WithAffiant</c> may wrap an <c>AIAgent</c> whose tool set
    /// it cannot enumerate — i.e. <c>agent.GetService(typeof(ChatOptions))</c> returns <c>null</c>,
    /// which happens for any <c>AIAgent</c> shape other than <c>ChatClientAgent</c> (framework spec /
    /// proposal §4.6: "detection before first run is the invariant, not the mechanism"). When this
    /// probe fails, Affiant cannot audit for uncovered hosted/provider-side tools at all, so
    /// <c>WithAffiant</c> throws by default. Setting this to <c>true</c> permits the wrap and emits a
    /// startup telemetry warning, mirroring <see cref="AcknowledgeUncoveredTools"/>'s explicit,
    /// auditable, loud acknowledgment shape. Default: <c>false</c> (every unauditable agent shape is
    /// refused).
    /// </summary>
    public bool AllowUnauditableAgent { get; set; }
}
