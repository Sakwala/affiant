namespace Affiant.AgentFramework.Validation;

using Affiant.AgentFramework.Extensions;
using Affiant.Core.Observability;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// Structural honesty check for MAF's coverage boundary (framework spec / proposal §4.6): MAF's
/// function-calling middleware fires only for client-invoked <see cref="AIFunction"/> tools.
/// Hosted/provider-side tools (hosted MCP, code interpreter, web/file search, and similar
/// server-executed toolboxes) bypass it entirely — Affiant cannot see, tag, or gate their writes.
///
/// Default: refuse, naming every uncovered tool. Override: <see cref="AgentFrameworkOptions.AcknowledgeUncoveredTools"/>
/// names tools the host explicitly accepts as uncovered; each acknowledged tool emits a startup
/// telemetry warning so the acknowledgment is auditable, never silent.
/// </summary>
internal static class HostedToolAudit
{
    public static void Run(AIAgent agent, AgentFrameworkOptions options, ILogger logger)
    {
        var chatOptions = agent.GetService(typeof(ChatOptions)) as ChatOptions;
        var tools = chatOptions?.Tools;
        if (tools is null || tools.Count == 0) return;

        var acknowledged = new HashSet<string>(options.AcknowledgeUncoveredTools, StringComparer.Ordinal);
        var refused = new List<string>();

        foreach (var tool in tools)
        {
            if (tool is AIFunction) continue; // client-invoked — covered by the middleware

            var name = tool.Name;
            if (acknowledged.Contains(name))
            {
                using var span = AffiantTelemetry.AffiantActivitySource
                    .StartActivity("agentframework.hosted_tool_acknowledged");
                span?.SetTag("affiant.hosted_tool.name", name);

                logger.LogWarning(
                    "Affiant.AgentFramework: hosted tool '{ToolName}' is acknowledged as uncovered by " +
                    "AgentFrameworkOptions.AcknowledgeUncoveredTools — Affiant cannot see, tag, or gate " +
                    "writes made through it.",
                    name);
                continue;
            }

            refused.Add(name);
        }

        if (refused.Count > 0)
        {
            throw new InvalidOperationException(
                "Affiant.AgentFramework: WithAffiant refuses to wrap an agent with uncovered hosted/provider-side " +
                $"tools: {string.Join(", ", refused)}. MAF's function-calling middleware fires only for " +
                "client-invoked AIFunction tools; hosted MCP, code interpreter, web/file search, and other " +
                "provider-executed tools run outside it, so Affiant cannot see, tag, or gate writes made through " +
                "them. Acknowledge each tool explicitly via AgentFrameworkOptions.AcknowledgeUncoveredTools if the " +
                "host accepts this coverage gap, or remove the tool from the agent.");
        }
    }
}
