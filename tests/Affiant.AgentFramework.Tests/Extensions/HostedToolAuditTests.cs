namespace Affiant.AgentFramework.Tests.Extensions;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
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

    // ── Registry is not mutated on a refused wrap (audit runs before registration) ──────────

    [Fact]
    public void RefusedWrap_WithNonEmptyCatalog_LeavesRegistryUnchanged()
    {
        var services = BuildServices();
        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var catalog = AffiantToolCatalog.FromType<SampleTools>();
        Assert.NotEmpty(catalog.Descriptors);

        var agent = BuildAgentWithHostedTool();

        Assert.Throws<InvalidOperationException>(() => agent.WithAffiant(sp, catalog));

        // The audit refuses before any registry mutation, so nothing from the catalog leaked in.
        Assert.Empty(registry.All);
    }

    [Fact]
    public void CorrectedRetryAfterRefusal_Succeeds_WithoutAlreadyRegistered()
    {
        var services = BuildServices();
        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var catalog = AffiantToolCatalog.FromType<SampleTools>();

        var refusedAgent = BuildAgentWithHostedTool();
        Assert.Throws<InvalidOperationException>(() => refusedAgent.WithAffiant(sp, catalog));

        // Corrected retry on the same singleton registry: an agent with no uncovered hosted tool.
        // If the refused wrap had registered the catalog's descriptors, this would die with
        // "already registered" from AffiantToolRegistry.Register.
        var pingTool = AIFunctionFactory.Create((Func<string>)(() => "ok"), name: "Ping");
        var correctedAgent = new ChatClientAgent(new NoOpChatClient(), instructions: "x", tools: [pingTool]);

        var wrapped = correctedAgent.WithAffiant(sp, catalog);

        Assert.NotNull(wrapped);
        Assert.Equal(catalog.Descriptors.Count, registry.All.Count);
    }

    [Fact]
    public void UnauditableAgentShape_CausesWithAffiantToThrow_ByDefault()
    {
        var services = BuildServices();
        var sp = services.BuildServiceProvider();

        var agent = new UnauditableAgent();

        var ex = Assert.Throws<InvalidOperationException>(
            () => agent.WithAffiant(sp, AffiantToolCatalog.FromType<NoToolsMarker>()));

        Assert.Contains("ChatOptions", ex.Message);
        Assert.Contains(nameof(AgentFrameworkOptions.AllowUnauditableAgent), ex.Message);
    }

    [Fact]
    public void UnauditableAgentShape_WithAllowUnauditableAgent_DoesNotThrow_AndLogsWarning()
    {
        var services = BuildServices(opts => opts.AllowUnauditableAgent = true);
        var logger = new CapturingLogger();
        services.AddSingleton<ILoggerFactory>(new CapturingLoggerFactory(logger));
        var sp = services.BuildServiceProvider();

        var agent = new UnauditableAgent();

        var wrapped = agent.WithAffiant(sp, AffiantToolCatalog.FromType<NoToolsMarker>());

        Assert.NotNull(wrapped);
        Assert.Contains(logger.Warnings, w => w.Contains("ChatOptions", StringComparison.Ordinal));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal non-<see cref="ChatClientAgent"/> <see cref="AIAgent"/> shape that answers <c>null</c>
    /// to Affiant's <c>typeof(ChatOptions)</c> tool-enumeration probe — reproducing the probe-fails
    /// condition audited by <see cref="Affiant.AgentFramework.Validation.HostedToolAudit"/> — while
    /// still answering a <see cref="FunctionInvokingChatClient"/> for
    /// <see cref="AIAgent.GetService(Type, object?)"/>, which MAF's own
    /// <c>AIAgentBuilder.Use(...)</c> requires before it will attach function-invocation middleware
    /// (<c>FunctionInvocationDelegatingAgentBuilderExtensions.Use</c> throws otherwise). The audit
    /// runs entirely at <c>WithAffiant</c> wrap time, so none of the run/session template methods are
    /// ever invoked by these tests; they throw if called only as a defensive tripwire.
    /// </summary>
    private sealed class UnauditableAgent : AIAgent
    {
        private readonly FunctionInvokingChatClient _functionInvokingChatClient = new(new NoOpChatClient());

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(FunctionInvokingChatClient)
                ? _functionInvokingChatClient
                : null;

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("UnauditableAgent does not support running.");

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("UnauditableAgent does not support streaming.");

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("UnauditableAgent does not support sessions.");

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("UnauditableAgent does not support sessions.");

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedSession,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("UnauditableAgent does not support sessions.");
    }

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

    private sealed class SampleTools
    {
        public string DoThing(string value) => value;
    }

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
