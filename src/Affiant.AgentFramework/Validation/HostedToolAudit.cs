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
///
/// Enumeration itself can fail: <c>agent.GetService(typeof(ChatOptions))</c> answers non-null only
/// for <c>ChatClientAgent</c> (the only concrete <c>AIAgent</c> <c>Microsoft.Agents.AI</c> 1.13.0
/// ships). Detection before first run is the invariant (§4.6), not the probe mechanism, so a null
/// probe result is itself an uncovered-audit condition — refused by default, mirroring
/// <see cref="AgentFrameworkOptions.AcknowledgeUncoveredTools"/>'s acknowledge-and-warn shape via
/// <see cref="AgentFrameworkOptions.AllowUnauditableAgent"/>.
/// </summary>
internal static class HostedToolAudit
{
    public static void Run(AIAgent agent, AgentFrameworkOptions options, ILogger logger)
    {
        var chatOptions = agent.GetService(typeof(ChatOptions)) as ChatOptions;

        if (chatOptions is null)
        {
            if (!options.AllowUnauditableAgent)
            {
                throw new InvalidOperationException(
                    "Affiant.AgentFramework: WithAffiant cannot audit hosted-tool coverage for this agent " +
                    $"shape ('{agent.GetType()}') — agent.GetService(typeof(ChatOptions)) returned null. " +
                    "Detection before first run is the invariant (proposal §4.6); Affiant refuses to wrap an " +
                    "agent whose tool set it cannot enumerate. Set " +
                    "AgentFrameworkOptions.AllowUnauditableAgent = true if the host accepts this coverage gap " +
                    "for this agent shape.");
            }

            using var unauditableSpan = AffiantTelemetry.AffiantActivitySource
                .StartActivity("agentframework.unauditable_agent_acknowledged");
            unauditableSpan?.SetTag("affiant.agent.type", agent.GetType().FullName);

            logger.LogWarning(
                "Affiant.AgentFramework: agent type '{AgentType}' does not expose ChatOptions via " +
                "GetService, so Affiant cannot audit it for uncovered hosted/provider-side tools. " +
                "Acknowledged via AgentFrameworkOptions.AllowUnauditableAgent.",
                agent.GetType());
            return;
        }

        var tools = chatOptions.Tools;
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
