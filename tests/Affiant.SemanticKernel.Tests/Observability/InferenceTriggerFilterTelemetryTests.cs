namespace Affiant.SemanticKernel.Tests.Observability;

using System.Diagnostics;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Verifies that InferenceTriggerFilter emits the correct OTel span events
/// (inference.triggered / inference.skipped) and uses the correct attribute keys.
/// Events are captured via an ActivityListener subscribed to Affiant.Framework.
/// Activity.Current during filter execution is the root test span started below.
/// </summary>
public class InferenceTriggerFilterTelemetryTests
{
    // ── Listener helpers ──────────────────────────────────────────────────────

    private static (ActivityListener Listener, Activity? Root) StartListening()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name is "Affiant.Framework" or "Affiant.TaskInference",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        // StartActivity returns non-null because a listener is now registered.
        var root = AffiantTelemetry.AffiantActivitySource.StartActivity("test_root");
        return (listener, root);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerFired_EmitsInferenceTriggered_WithAllFourAttributes()
    {
        var (listener, root) = StartListening();

        try
        {
            var (filter, kernel) = BuildHarness(
                triggerResult: true,
                descriptor: new AffiantToolDescriptor("WriteFn", "TestPlugin",
                    Operation.WriteCreate, "Thing", typeof(StubStrategy)));
            kernel.FunctionInvocationFilters.Add(filter);
            kernel.Data["ConversationId"] = "conv-trigger";
            kernel.Data["AffiantTurnNumber"] = 0;
            kernel.Data["ChatHistory"] = new ChatHistory();

            await kernel.InvokeAsync("TestPlugin", "WriteFn");
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        var events = root?.Events.ToList() ?? [];
        Assert.True(events.Any(e => e.Name == "inference.triggered"),
            "Expected inference.triggered event to be emitted");
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
            var (filter, kernel) = BuildHarness(
                triggerResult: false,
                descriptor: new AffiantToolDescriptor("WriteFn", "TestPlugin",
                    Operation.WriteCreate, "Thing", typeof(StubStrategy)));
            kernel.FunctionInvocationFilters.Add(filter);

            await kernel.InvokeAsync("TestPlugin", "WriteFn");
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        var events = root?.Events.ToList() ?? [];
        Assert.True(events.Any(e => e.Name == "inference.skipped"),
            "Expected inference.skipped event to be emitted");
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
            var (filter, kernel) = BuildHarness(
                triggerResult: true,
                descriptor: new AffiantToolDescriptor("WriteFn", "TestPlugin",
                    Operation.WriteCreate, "Thing", InferenceStrategy: null));
            kernel.FunctionInvocationFilters.Add(filter);
            kernel.Data["ConversationId"] = "conv-nostrategy";
            kernel.Data["AffiantTurnNumber"] = 0;
            kernel.Data["ChatHistory"] = new ChatHistory();

            await kernel.InvokeAsync("TestPlugin", "WriteFn");
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        var events = root?.Events.ToList() ?? [];
        Assert.True(events.Any(e => e.Name == "inference.skipped"),
            "Expected inference.skipped event to be emitted");
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
            // No descriptor registered — framework doesn't know about this function.
            var (filter, kernel) = BuildHarness(triggerResult: false, descriptor: null);
            kernel.FunctionInvocationFilters.Add(filter);

            await kernel.InvokeAsync("TestPlugin", "WriteFn");
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        var events = root?.Events.ToList() ?? [];
        Assert.DoesNotContain(events, e => e.Name.StartsWith("inference.", StringComparison.Ordinal));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (InferenceTriggerFilter Filter, Kernel Kernel) BuildHarness(
        bool triggerResult, AffiantToolDescriptor? descriptor)
    {
        var fabric = new ContextFabric();
        var strategy = new StubStrategy();
        var step = new TaskInferenceStep(strategy, fabric, NullLogger<TaskInferenceStep>.Instance);
        var port = new NoOpPort();
        var runner = new TaskInferenceRunner(port, fabric, step, NullLogger<TaskInferenceRunner>.Instance);

        var registry = new AffiantToolRegistry();
        if (descriptor is not null)
            registry.Register(descriptor);

        var services = new ServiceCollection();
        services.AddSingleton<IContextFabric>(fabric);
        services.AddSingleton<ITaskInferenceStrategy>(strategy);
        services.AddSingleton<StubStrategy>(strategy);
        var sp = services.BuildServiceProvider();

        var filter = new InferenceTriggerFilter(
            [new FakeTrigger(_ => triggerResult)], runner, sp, registry,
            NullLogger<InferenceTriggerFilter>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("TestPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => "fn-result", "WriteFn")]));

        return (filter, kernel);
    }

    private sealed class FakeTrigger(Func<InferenceTriggerContext, bool> impl) : IInferenceTrigger
    {
        public bool ShouldRun(InferenceTriggerContext context) => impl(context);
    }

    private sealed class StubStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";
        public IReadOnlyList<TaskInferenceField> Fields =>
            [new TaskInferenceField("title", "string", "Title")];
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
