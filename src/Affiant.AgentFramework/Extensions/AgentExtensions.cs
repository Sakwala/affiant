namespace Affiant.AgentFramework.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.AgentFramework.Filters;
using Affiant.AgentFramework.Validation;
using Affiant.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The single blessed way to attach Affiant to an <see cref="AIAgent"/> (proposal §4.5). Wrapping
/// produces a new agent instance — the pre-wrap <paramref name="agent"/> silently bypasses Affiant
/// if a host retains it, so this method returns the wrapped instance and hosts must use only that.
/// </summary>
public static class AgentExtensions
{
    /// <summary>
    /// Registers <paramref name="tools"/>' descriptors with <see cref="IAffiantToolRegistry"/>,
    /// attaches the neutral tool-invocation pipeline as MAF function-calling middleware, runs the
    /// hosted-tool coverage audit (see <see cref="HostedToolAudit"/>), and returns the wrapped agent.
    /// Requires <c>services.AddAffiantCore()</c> and <c>services.AddAffiantAgentFramework()</c> to
    /// have been called first.
    /// </summary>
    public static AIAgent WithAffiant(
        this AIAgent agent,
        IServiceProvider services,
        AffiantToolCatalog tools)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tools);

        var registry = services.GetService<IAffiantToolRegistry>()
            ?? throw new InvalidOperationException(
                "Affiant.AgentFramework: IAffiantToolRegistry is not registered. " +
                "Call services.AddAffiantCore() before agent.WithAffiant().");

        var pipeline = services.GetService<ToolInvocationPipeline>()
            ?? throw new InvalidOperationException(
                "Affiant.AgentFramework: ToolInvocationPipeline is not registered. " +
                "Call services.AddAffiantCore() before agent.WithAffiant().");

        var options = services.GetService<AgentFrameworkOptions>()
            ?? throw new InvalidOperationException(
                "Affiant.AgentFramework: AgentFrameworkOptions is not registered. " +
                "Call services.AddAffiantAgentFramework() before agent.WithAffiant().");

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Affiant.AgentFramework")
            ?? NullLogger.Instance;

        // Audit before any registry mutation: a refused wrap (unacknowledged hosted tool or
        // unauditable agent shape) must leave the singleton registry untouched, so a corrected
        // retry does not die with "already registered" from AffiantToolRegistry.Register.
        HostedToolAudit.Run(agent, options, logger);

        foreach (var descriptor in tools.Descriptors)
            registry.Register(descriptor);

        var middleware = new AffiantFunctionInvocationMiddleware(pipeline, registry);

        return agent.AsBuilder()
            .Use(middleware.InvokeAsync)
            .Build(services);
    }
}
