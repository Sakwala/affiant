namespace Affiant.Extensions.AI.Extensions;

/// <summary>
/// Host-facing switches for the Microsoft.Extensions.AI adapter, supplied to
/// <see cref="ServiceCollectionExtensions.AddAffiantExtensionsAI"/>.
///
/// <para>
/// Deliberately smaller than <c>Affiant.AgentFramework</c>'s <c>AgentFrameworkOptions</c>: that type
/// also carries <c>AllowUnauditableAgent</c>, an escape hatch that exists only because the Microsoft
/// Agent Framework hides <c>ChatOptions</c> behind an opaque <c>AIAgent</c> whose tool set cannot
/// always be enumerated before the first run. This adapter has no such opacity — the host constructs
/// the <see cref="Microsoft.Extensions.AI.ChatOptions"/> and hands it to
/// <see cref="ChatOptionsExtensions.WithAffiant"/> directly, so the tool list is always enumerable
/// and there is nothing to acknowledge (design brief
/// <c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>, decision 5).
/// </para>
/// </summary>
public sealed class ExtensionsAIOptions
{
    /// <summary>
    /// LLM-visible names of hosted/provider-executed tools the host explicitly accepts as being
    /// outside Affiant's coverage. Each acknowledged tool emits a wire-up telemetry span and a
    /// warning log, so the acknowledgment is auditable and never silent; every unacknowledged one
    /// makes <see cref="ChatOptionsExtensions.WithAffiant"/> throw. See
    /// <c>Affiant.Extensions.AI.Validation.HostedToolAudit</c> for what "uncovered" means and why the
    /// default is refusal.
    /// </summary>
    public IReadOnlyList<string> AcknowledgeUncoveredTools { get; set; } = [];
}
