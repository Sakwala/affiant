namespace Affiant.AgentFramework.Tests.Extensions;

using Affiant.AgentFramework.Extensions;
using Affiant.AgentFramework.Tests.Utilities;
using Affiant.Core.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Hosted-tool coverage audit (framework spec / proposal §4.6): WithAffiant refuses an agent
/// carrying uncovered hosted/provider-side tools by default, allows them when explicitly
/// acknowledged via <see cref="AgentFrameworkOptions.AcknowledgeUncoveredTools"/>, and emits a
/// telemetry warning for each acknowledgment. Fires before first run — audit happens inside
/// WithAffiant itself, not lazily on first RunAsync.
/// </summary>
public class HostedToolAuditTests
{
    [Fact]
    public void UnacknowledgedHostedTool_CausesWithAffiantToThrow()
    {
        var services = BuildServices();
        var sp = services.BuildServiceProvider();

        var agent = BuildAgentWithHostedTool();

        var ex = Assert.Throws<InvalidOperationException>(
            () => agent.WithAffiant(sp, AffiantToolCatalog.FromType<NoToolsMarker>()));

        Assert.Contains("code_interpreter", ex.Message);
    }

    [Fact]
    public void AcknowledgedHostedTool_DoesNotThrow_AndLogsWarning()
    {
        var services = BuildServices(opts => opts.AcknowledgeUncoveredTools = ["code_interpreter"]);
        var logger = new CapturingLogger();
        services.AddSingleton<ILoggerFactory>(new CapturingLoggerFactory(logger));
        var sp = services.BuildServiceProvider();

        var agent = BuildAgentWithHostedTool();

        var wrapped = agent.WithAffiant(sp, AffiantToolCatalog.FromType<NoToolsMarker>());

        Assert.NotNull(wrapped);
        Assert.Contains(logger.Warnings, w => w.Contains("code_interpreter", StringComparison.Ordinal));
    }

    [Fact]
    public void NoHostedTools_DoesNotThrow()
    {
        var services = BuildServices();
        var sp = services.BuildServiceProvider();

        var tool = AIFunctionFactory.Create((Func<string>)(() => "ok"), name: "Ping");
        var agent = new ChatClientAgent(new NoOpChatClient(), instructions: "x", tools: [tool]);

        var wrapped = agent.WithAffiant(sp, AffiantToolCatalog.FromType<NoToolsMarker>());
        Assert.NotNull(wrapped);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IServiceCollection BuildServices(Action<AgentFrameworkOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddAffiantAgentFramework(configure);
        services.AddSingleton<IChatClient>(new NoOpChatClient());
        return services;
    }

    private static AIAgent BuildAgentWithHostedTool()
    {
        var codeInterpreter = new HostedCodeInterpreterTool();
        return new ChatClientAgent(new NoOpChatClient(), instructions: "x", tools: [codeInterpreter]);
    }

    private sealed class NoToolsMarker;

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class CapturingLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}
