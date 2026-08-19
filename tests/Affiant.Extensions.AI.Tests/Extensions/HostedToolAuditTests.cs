namespace Affiant.Extensions.AI.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Extensions;
using Affiant.Extensions.AI.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Hosted-tool coverage audit (design decision 5 of the M.E.AI adapter brief,
/// <c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>).
///
/// <para>
/// The rule these pin: a tool Affiant cannot wrap is a tool Affiant cannot govern, and the host must
/// be told at wire-up rather than discover it from the write that slipped through. Hosted markers
/// (<see cref="HostedCodeInterpreterTool"/>, <see cref="HostedWebSearchTool"/>, hosted MCP, …) are
/// <see cref="AITool"/>s with no client-side invocation surface, so there is nothing to wrap.
/// </para>
///
/// <para>
/// Deliberately the MAF suite's shape minus two tests: MAF additionally covers an "agent shape whose
/// ChatOptions cannot be probed" and its <c>AllowUnauditableAgent</c> escape hatch. Neither case can
/// arise here — the host hands us the <see cref="ChatOptions"/>, so the tool list is always
/// enumerable. That absence is itself the decision-5 claim, asserted below by
/// <see cref="ExtensionsAIOptions"/> carrying no such switch.
/// </para>
/// </summary>
public class HostedToolAuditTests
{
    [Fact]
    public void UnacknowledgedHostedTool_CausesWithAffiantToThrow()
    {
        var sp = BuildServices().BuildServiceProvider();
        var options = new ChatOptions { Tools = [new HostedCodeInterpreterTool()] };

        var ex = Assert.Throws<InvalidOperationException>(
            () => options.WithAffiant(sp, AffiantToolCatalog.FromType<NoToolsMarker>()));

        Assert.Contains("code_interpreter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcknowledgedHostedTool_DoesNotThrow_AndLogsWarning()
    {
        var logger = new CapturingLogger();
        var services = BuildServices(opts => opts.AcknowledgeUncoveredTools = ["code_interpreter"]);
        services.AddSingleton<ILoggerFactory>(new CapturingLoggerFactory(logger));
        var sp = services.BuildServiceProvider();

        var options = new ChatOptions { Tools = [new HostedCodeInterpreterTool()] };

        var wired = options.WithAffiant(sp, AffiantToolCatalog.FromType<NoToolsMarker>());

        Assert.NotNull(wired);
        Assert.Contains(logger.Warnings, w => w.Contains("code_interpreter", StringComparison.Ordinal));
    }

    /// <summary>
    /// An acknowledged hosted tool is still handed to the provider — acknowledging the coverage gap
    /// must not quietly delete the tool the host asked for.
    /// </summary>
    [Fact]
    public void AcknowledgedHostedTool_IsPassedThroughUnwrapped()
    {
        var services = BuildServices(opts => opts.AcknowledgeUncoveredTools = ["web_search"]);
        var sp = services.BuildServiceProvider();

        var hosted = new HostedWebSearchTool();
        var options = new ChatOptions { Tools = [hosted] };

        var wired = options.WithAffiant(sp, AffiantToolCatalog.FromType<NoToolsMarker>());

        Assert.Same(hosted, Assert.Single(wired.Tools!));
    }

    [Fact]
    public void NoHostedTools_DoesNotThrow()
    {
        var sp = BuildServices().BuildServiceProvider();
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((Func<string>)(() => "ok"), name: "Ping")],
        };

        var wired = options.WithAffiant(sp, AffiantToolCatalog.FromType<NoToolsMarker>());

        Assert.NotNull(wired);
    }

    [Fact]
    public void EmptyToolList_DoesNotThrow()
    {
        var sp = BuildServices().BuildServiceProvider();

        var wired = new ChatOptions().WithAffiant(sp, AffiantToolCatalog.FromType<NoToolsMarker>());

        Assert.NotNull(wired);
    }

    // ── Refusal is a no-op: the audit runs before any registry mutation ───────

    [Fact]
    public void RefusedWiring_WithNonEmptyCatalog_LeavesRegistryUnchanged()
    {
        var sp = BuildServices().BuildServiceProvider();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var catalog = AffiantToolCatalog.FromType<SampleTools>();
        Assert.NotEmpty(catalog.Descriptors);

        var options = new ChatOptions { Tools = [new HostedCodeInterpreterTool()] };

        Assert.Throws<InvalidOperationException>(() => options.WithAffiant(sp, catalog));

        Assert.Empty(registry.All);
    }

    /// <summary>
    /// The reason ordering matters: <see cref="IAffiantToolRegistry"/> is a singleton and rejects a
    /// second registration of the same descriptor. Had the refused wiring registered the catalog,
    /// the corrected retry below would die with "already registered" — turning one actionable error
    /// into a second, misleading one.
    /// </summary>
    [Fact]
    public void CorrectedRetryAfterRefusal_Succeeds_WithoutAlreadyRegistered()
    {
        var sp = BuildServices().BuildServiceProvider();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var catalog = AffiantToolCatalog.FromType<SampleTools>();

        Assert.Throws<InvalidOperationException>(
            () => new ChatOptions { Tools = [new HostedCodeInterpreterTool()] }.WithAffiant(sp, catalog));

        var corrected = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((Func<string>)(() => "ok"), name: "Ping")],
        }.WithAffiant(sp, catalog);

        Assert.NotNull(corrected);
        Assert.Equal(catalog.Descriptors.Count, registry.All.Count);
    }

    /// <summary>
    /// Decision 5's simplification, pinned as a fact rather than a comment: this adapter's options
    /// type carries no unauditable-shape escape hatch, because the shape it would guard against
    /// cannot occur when the host supplies the <see cref="ChatOptions"/> itself.
    /// </summary>
    [Fact]
    public void ExtensionsAIOptions_HasNoUnauditableEscapeHatch()
    {
        var properties = typeof(ExtensionsAIOptions)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        Assert.Equal([nameof(ExtensionsAIOptions.AcknowledgeUncoveredTools)], properties);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IServiceCollection BuildServices(Action<ExtensionsAIOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddAffiantExtensionsAI(configure);
        return services;
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

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class CapturingLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose() { }
    }
}
