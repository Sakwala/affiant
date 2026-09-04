namespace Affiant.Extensions.AI.Tests.Extensions;

using System.Diagnostics;
using Affiant.Abstractions.Telemetry;
using Affiant.Core.Extensions;
using Affiant.Core.Observability;
using Affiant.Extensions.AI.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// CV-4's registry event: a tool the gate cannot cover raises <c>coverage.refused</c> before the
/// wire-up refusal is thrown. The exception tells the developer who is looking at the console; the
/// event tells the operator who is looking at a dashboard which uncovered tools adopters keep
/// trying to wire up.
/// </summary>
public class CoverageRefusalTelemetryTests
{
    [Fact]
    public void AnUncoveredHostedTool_EmitsCoverageRefusedBeforeItRefuses()
    {
        using var probe = new TelemetryProbe();
        var provider = BuildServices().BuildServiceProvider();
        var options = new ChatOptions { Tools = [new HostedCodeInterpreterTool()] };

        Assert.Throws<Affiant.Abstractions.Exceptions.AffiantCoverageException>(
            () => options.WithAffiant(provider, AffiantToolCatalog.FromType<NoTools>()));

        var attributes = probe.Attributes(TelemetryKeys.CoverageRefused);
        Assert.Equal("code_interpreter", attributes[TelemetryKeys.Attributes.GenAiToolName]);
        // One of CV-4's own three categories, not an adapter's word for the same thing: a collector
        // counting coverage refusals across two adapters and the core has to be counting the same
        // set. A hosted/provider-side tool is `provider-executed`.
        Assert.Equal("provider-executed", attributes[TelemetryKeys.Attributes.CoverageCategory]);
        Assert.Equal("wire-up", attributes[TelemetryKeys.Attributes.Phase]);
    }

    /// <summary>
    /// An acknowledged tool is a coverage gap the host has taken on deliberately, not a refusal.
    /// It is already warned about and traced; emitting a refusal event for it would make a
    /// refusal-rate alert fire on a decision the host has already made.
    /// </summary>
    [Fact]
    public void AnAcknowledgedHostedTool_IsNotARefusal()
    {
        using var probe = new TelemetryProbe();
        var provider = BuildServices(opts => opts.AcknowledgeUncoveredTools = ["code_interpreter"])
            .BuildServiceProvider();
        var options = new ChatOptions { Tools = [new HostedCodeInterpreterTool()] };
        options.WithAffiant(provider, AffiantToolCatalog.FromType<NoTools>());

        Assert.False(probe.Saw(TelemetryKeys.CoverageRefused));
    }

    private static IServiceCollection BuildServices(Action<ExtensionsAIOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddAffiantExtensionsAI(configure);
        return services;
    }

    private sealed class NoTools;

    /// <summary>
    /// The same isolated listener the Core suite uses — see its copy for why the source is touched
    /// before the listener is registered (repo issue #17).
    /// </summary>
    private sealed class TelemetryProbe : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Activity? _root;

        public TelemetryProbe()
        {
            var source = AffiantTelemetry.AffiantActivitySource;

            _listener = new ActivityListener
            {
                ShouldListenTo = candidate => ReferenceEquals(candidate, source),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(_listener);
            _root = source.StartActivity("test_root");
        }

        public IReadOnlyList<ActivityEvent> Events => _root?.Events.ToList() ?? [];

        public bool Saw(string name) => Events.Any(e => e.Name == name);

        public IReadOnlyDictionary<string, object?> Attributes(string name) =>
            Events.Single(e => e.Name == name).Tags.ToDictionary(t => t.Key, t => t.Value);

        public void Dispose()
        {
            _root?.Dispose();
            _listener.Dispose();
        }
    }
}
