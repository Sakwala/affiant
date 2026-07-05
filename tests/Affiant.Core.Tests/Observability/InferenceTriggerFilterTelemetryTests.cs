namespace Affiant.Core.Tests.Observability;

using System.Diagnostics;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Verifies that the neutral InferenceTriggerFilter emits the correct OTel span events
/// (inference.triggered / inference.skipped) with the correct attribute keys. Events are captured
/// on the root test span via an ActivityListener. Backend-free — invokes the filter directly.
/// </summary>
public class InferenceTriggerFilterTelemetryTests
{
    private static (ActivityListener Listener, Activity? Root) StartListening()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name is "Affiant.Framework" or "Affiant.TaskInference",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var root = AffiantTelemetry.AffiantActivitySource.StartActivity("test_root");
        return (listener, root);
    }

    private static async Task RunFilter(InferenceTriggerFilter filter, IServiceProvider services)
    {
        var ctx = new ToolInvocationContext
        {
            FunctionName = "WriteFn",
            PluginName = "TestPlugin",
            Arguments = new Dictionary<string, object?>(),
            Services = services,
            ConversationId = "conv-tel",
            TurnNumber = 0,
            History = Array.Empty<AffiantChatMessage>(),
        };
        await filter.OnToolInvocationAsync(ctx, _ => Task.CompletedTask);
    }

    [Fact]
    public async Task TriggerFired_EmitsInferenceTriggered_WithAllFourAttributes()
    {
        var (listener, root) = StartListening();
        try
        {
            var (filter, sp) = BuildHarness(true,
                new AffiantToolDescriptor("WriteFn", "TestPlugin", Operation.WriteCreate, "Thing", typeof(StubStrategy)));
            await RunFilter(filter, sp);
        }
        finally { root?.Dispose(); listener.Dispose(); }

        var events = root?.Events.ToList() ?? [];
        var triggered = events.Single(e => e.Name == "inference.triggered");
        var tags = triggered.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal("WriteFn", tags["affiant.function.name"]?.ToString());
        Assert.Equal("TestPlugin", tags["affiant.plugin.name"]?.ToString());
        Assert.Equal("Thing", tags["affiant.entity.type"]?.ToString());
        Assert.Contains("StubStrategy", tags["affiant.strategy.type"]?.ToString());
    }

    [Fact]
    public async Task NoTriggerFired_DescriptorInRegistry_EmitsInferenceSkipped_NotAWriteTool()
    {
        var (listener, root) = StartListening();
        try
        {
            var (filter, sp) = BuildHarness(false,
                new AffiantToolDescriptor("WriteFn", "TestPlugin", Operation.WriteCreate, "Thing", typeof(StubStrategy)));
            await RunFilter(filter, sp);
        }
        finally { root?.Dispose(); listener.Dispose(); }

        var events = root?.Events.ToList() ?? [];
        var skipped = events.Single(e => e.Name == "inference.skipped");
        var tags = skipped.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal("WriteFn", tags["affiant.function.name"]?.ToString());
        Assert.Equal("not_a_write_tool", tags["affiant.skip.reason"]?.ToString());
    }

    [Fact]
    public async Task TriggerFired_DescriptorHasNullStrategy_EmitsInferenceSkipped_NoStrategyRegistered()
    {
        var (listener, root) = StartListening();
        try
        {
            var (filter, sp) = BuildHarness(true,
                new AffiantToolDescriptor("WriteFn", "TestPlugin", Operation.WriteCreate, "Thing", InferenceStrategy: null));
            await RunFilter(filter, sp);
        }
        finally { root?.Dispose(); listener.Dispose(); }

        var events = root?.Events.ToList() ?? [];
        var skipped = events.Single(e => e.Name == "inference.skipped");
        var tags = skipped.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal("WriteFn", tags["affiant.function.name"]?.ToString());
        Assert.Equal("no_strategy_registered", tags["affiant.skip.reason"]?.ToString());
    }

    [Fact]
    public async Task NoDescriptor_NoInferenceEventEmitted()
    {
        var (listener, root) = StartListening();
        try
        {
            var (filter, sp) = BuildHarness(false, descriptor: null);
            await RunFilter(filter, sp);
        }
        finally { root?.Dispose(); listener.Dispose(); }

        var events = root?.Events.ToList() ?? [];
        Assert.DoesNotContain(events, e => e.Name.StartsWith("inference.", StringComparison.Ordinal));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (InferenceTriggerFilter Filter, IServiceProvider Services) BuildHarness(
        bool triggerResult, AffiantToolDescriptor? descriptor)
    {
        var fabric = new ContextFabric();
        var strategy = new StubStrategy();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var runner = new TaskInferenceRunner(new NoOpPort(), fabric, step, NullLogger<TaskInferenceRunner>.Instance);

        var registry = new AffiantToolRegistry();
        if (descriptor is not null)
            registry.Register(descriptor);

        var services = new ServiceCollection();
        services.AddSingleton<IContextFabric>(fabric);
        services.AddSingleton<ITaskInferenceStrategy>(strategy);
        services.AddSingleton<StubStrategy>(strategy);
        var sp = services.BuildServiceProvider();

        var filter = new InferenceTriggerFilter(
            [new FakeTrigger(_ => triggerResult)], runner, fabric, registry,
            NullLogger<InferenceTriggerFilter>.Instance);

        return (filter, sp);
    }

    private sealed class FakeTrigger(Func<InferenceTriggerContext, bool> impl) : IInferenceTrigger
    {
        public bool ShouldRun(InferenceTriggerContext context) => impl(context);
    }

    private sealed class StubStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";
        public IReadOnlyList<TaskInferenceField> Fields => [new TaskInferenceField("title", "string", "Title")];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class NoOpPort : IInferenceCompletionPort
    {
        public Task<JsonElement> CompleteStructuredAsync(
            InferenceCompletionRequest request, CancellationToken cancellationToken = default)
        {
            using var doc = JsonDocument.Parse("""{"title":{"value":"test","confidence":0.9}}""");
            return Task.FromResult(doc.RootElement.Clone());
        }
    }
}
