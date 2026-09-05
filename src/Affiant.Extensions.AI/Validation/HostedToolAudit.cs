namespace Affiant.Extensions.AI.Validation;

using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// Structural honesty check for this adapter's coverage boundary: Affiant intercepts a tool by
/// <em>being</em> it — every covered tool is an <see cref="AIFunction"/> the host hands us and we
/// hand back wrapped. Hosted/provider-side tools (hosted MCP, code interpreter, web search, file
/// search, image generation, tool search) are not <see cref="AIFunction"/>s at all: they are
/// <see cref="AITool"/> markers that tell the provider it may run something server-side, with no
/// client-side invocation surface whatsoever. Nothing can wrap them, so Affiant cannot see, tag, or
/// gate the writes they make.
///
/// <para>
/// Default: refuse at wire-up, naming every uncovered tool. Override:
/// <see cref="ExtensionsAIOptions.AcknowledgeUncoveredTools"/> names tools the host explicitly
/// accepts as uncovered; each acknowledged tool emits a wire-up telemetry span and a warning so the
/// acknowledgment is auditable, never silent. Refusing at wire-up rather than at first use is the
/// same loudness rule <c>Affiant.AgentFramework</c>'s <c>HostedToolAudit</c>,
/// <c>Affiant.SemanticKernel</c>'s <c>AffiantStartupValidator</c> and <c>Affiant.Core</c>'s
/// <c>AffiantWireUpValidator</c> apply: a coverage gap must not be discoverable only by the write it
/// silently let through.
/// </para>
///
/// <para>
/// <b>Simpler than the MAF counterpart, deliberately</b> (design brief
/// <c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>, decision 5).
/// <c>Affiant.AgentFramework.Validation.HostedToolAudit</c> must first probe
/// <c>agent.GetService(typeof(ChatOptions))</c> and can get back null for agent shapes it cannot
/// introspect, which is why it needs an <c>AllowUnauditableAgent</c> escape hatch and a second
/// refusal path. Here the host constructs the <see cref="ChatOptions"/> and passes it in, so the
/// tool list is always enumerable: no probe, no unauditable case, no escape hatch.
/// </para>
/// </summary>
internal static class HostedToolAudit
{
    public static void Run(IReadOnlyList<AITool> tools, ExtensionsAIOptions options, ILogger logger)
    {
        if (tools.Count == 0) return;

        var acknowledged = new HashSet<string>(options.AcknowledgeUncoveredTools, StringComparer.Ordinal);
        var refused = new List<string>();

        foreach (var tool in tools)
        {
            if (tool is AIFunction) continue; // client-invoked — covered, because we wrap it

            var name = tool.Name;
            if (acknowledged.Contains(name))
            {
                using var span = AffiantTelemetry.AffiantActivitySource
                    .StartActivity("extensionsai.hosted_tool_acknowledged");
                span?.SetTag("affiant.hosted_tool.name", name);

                logger.LogWarning(
                    "Affiant.Extensions.AI: hosted tool '{ToolName}' is acknowledged as uncovered by " +
                    "ExtensionsAIOptions.AcknowledgeUncoveredTools — Affiant cannot see, tag, or gate " +
                    "writes made through it.",
                    name);
                continue;
            }

            refused.Add(name);
        }

        if (refused.Count > 0)
        {
            // The rule is the framework's — ToolCoverage.Refuse emits one `coverage.refused` event
            // per tool and throws the protocol's own refusal, carrying the `coverage-refused` code
            // (CV-4) — and the sentence about this adapter's wiring is this adapter's.
            ToolCoverage.Refuse(
                refused,
                CoverageCategory.ProviderExecuted,
                "Affiant intercepts a tool by wrapping the AIFunction the client invokes; hosted " +
                "MCP, code interpreter, web/file search, and other provider-executed tools are " +
                "AITool markers with no client-side invocation to wrap, so they run outside Affiant " +
                "entirely. Acknowledge each tool explicitly via " +
                "ExtensionsAIOptions.AcknowledgeUncoveredTools if the host accepts this coverage " +
                "gap, or remove the tool from ChatOptions.Tools.");
        }
    }
}
