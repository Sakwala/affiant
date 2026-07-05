namespace Affiant.Core.Tests.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Backend-free unit tests for the neutral InferenceTriggerFilter.
/// Verifies trigger evaluation, idempotency bookkeeping, strategy resolution,
/// fail-safe behavior, and the "next always fires" contract. The filter is invoked directly
/// with a hand-built <see cref="ToolInvocationContext"/>.
/// </summary>
public class InferenceTriggerFilterTests
{
    private sealed class Harness
    {
        public required InferenceTriggerFilter Filter { get; init; }
        public required ContextFabric Fabric { get; init; }
        public required IServiceProvider Services { get; init; }
        public required int[] InvocationCount { get; init; }

        public async Task<(bool NextRan, object? Result)> RunAsync(
            string conversationId = "conv", int turnNumber = 0, CancellationToken ct = default)
        {
            var ctx = new ToolInvocationContext
            {
                FunctionName = "WriteFn",
                PluginName = "TestPlugin",
                Arguments = new Dictionary<string, object?>(),
                Services = Services,
                ConversationId = conversationId,
                TurnNumber = turnNumber,
                History = Array.Empty<AffiantChatMessage>(),
            };
            var nextRan = false;
            await Filter.OnToolInvocationAsync(ctx, c => { nextRan = true; c.Result = "fn-result"; return Task.CompletedTask; }, ct);
            return (nextRan, ctx.Result);
        }
    }

    private static Harness BuildHarness(
        bool triggerResult = false,
        bool withDescriptor = false,
        IInferenceCompletionPort? port = null,
        IEnumerable<IInferenceTrigger>? triggers = null)
    {
        var invCount = new[] { 0 };
        var capturePort = port ?? new CapturingPort(invCount,
            """{"title": {"value": "inferred", "confidence": 0.8}}""");

        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var runner = new TaskInferenceRunner(capturePort, fabric, step, NullLogger<TaskInferenceRunner>.Instance);

        var registry = new AffiantToolRegistry();
        if (withDescriptor)
            registry.Register(new AffiantToolDescriptor(
                "WriteFn", "TestPlugin", new Operation("WriteCreate"), "TestEntity", typeof(StubStrategy)));

        var strategy = new StubStrategy();
        var services = new ServiceCollection();
        services.AddSingleton<IContextFabric>(fabric);
        services.AddSingleton<ITaskInferenceStrategy>(strategy);
        services.AddSingleton<StubStrategy>(strategy);
        var sp = services.BuildServiceProvider();

        var triggerList = triggers ?? [new FakeTrigger(_ => triggerResult)];

        var filter = new InferenceTriggerFilter(
            triggerList, runner, fabric, registry, NullLogger<InferenceTriggerFilter>.Instance);

        return new Harness { Filter = filter, Fabric = fabric, Services = sp, InvocationCount = invCount };
    }

    // ── Trigger evaluation ───────────────────────────────────────────────────

    [Fact]
    public async Task AllTriggersReturnFalse_SkipsInference_CallsNext()
    {
        var h = BuildHarness(triggerResult: false);
        var (nextRan, _) = await h.RunAsync();
        Assert.True(nextRan);
        Assert.Equal(0, h.InvocationCount[0]);
    }

    [Fact]
    public async Task FirstTriggerReturnsTrue_CallsRunnerOnce()
    {
        var h = BuildHarness(triggerResult: true, withDescriptor: true);
        await h.RunAsync("conv1", 0);
        Assert.Equal(1, h.InvocationCount[0]);
    }

    [Fact]
    public async Task MultipleTriggersFirstTrue_RunnerCalledOnce_ShortCircuits()
    {
        var evaluatedCount = 0;
        var triggers = new List<IInferenceTrigger>
        {
            new FakeTrigger(_ => { evaluatedCount++; return true; }),
            new FakeTrigger(_ => { evaluatedCount++; return true; }),
        };
        var h = BuildHarness(triggers: triggers, withDescriptor: true);
        await h.RunAsync("conv-multi", 0);

        Assert.Equal(1, evaluatedCount);
        Assert.Equal(1, h.InvocationCount[0]);
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SameConvFunctionTurn_RunnerCalledOnce()
    {
        var h = BuildHarness(triggerResult: true, withDescriptor: true);
        await h.RunAsync("conv-idem", 1);
        await h.RunAsync("conv-idem", 1);
        Assert.Equal(1, h.InvocationCount[0]);
    }

    [Fact]
    public async Task SameConvFunction_DifferentTurn_RunnerCalledTwice()
    {
        var h = BuildHarness(triggerResult: true, withDescriptor: true);
        await h.RunAsync("conv-turn", 1);
        await h.RunAsync("conv-turn", 2);
        Assert.Equal(2, h.InvocationCount[0]);
    }

    // ── Strategy resolution ───────────────────────────────────────────────────

    [Fact]
    public async Task NoDescriptor_LogsWarning_CallsNext_DoesNotThrow()
    {
        var h = BuildHarness(triggerResult: true, withDescriptor: false);
        var (nextRan, result) = await h.RunAsync("conv-nodesc", 0);
        Assert.True(nextRan);
        Assert.Equal("fn-result", result);
        Assert.Equal(0, h.InvocationCount[0]);
    }

    // ── Fail-safe ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunnerThrowsNonCancellation_LogsWarning_ContinuesToNextAndReturnsResult()
    {
        var h = BuildHarness(triggerResult: true, withDescriptor: true,
            port: new ThrowingPort(new InvalidOperationException("port failure")));
        var (nextRan, result) = await h.RunAsync("conv-fail", 0);
        Assert.True(nextRan);
        Assert.Equal("fn-result", result);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunnerThrowsCancellation_ExceptionPropagates_NextDoesNotFire()
    {
        var h = BuildHarness(triggerResult: true, withDescriptor: true,
            port: new ThrowingPort(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync("conv-cancel", 0));
    }

    // ── Next always fires ─────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCallAlwaysFires_EvenWhenTriggerReturnsFalse()
    {
        var h = BuildHarness(triggerResult: false);
        var (nextRan, result) = await h.RunAsync();
        Assert.True(nextRan);
        Assert.Equal("fn-result", result);
    }

    [Fact]
    public async Task ToolCallAlwaysFires_EvenAfterInference()
    {
        var h = BuildHarness(triggerResult: true, withDescriptor: true);
        var (nextRan, result) = await h.RunAsync("conv-always", 0);
        Assert.True(nextRan);
        Assert.Equal("fn-result", result);
    }

    // ── Test doubles ───────────────────────────────────────────────────────────

    private sealed class FakeTrigger(Func<InferenceTriggerContext, bool> impl) : IInferenceTrigger
    {
        public bool ShouldRun(InferenceTriggerContext context) => impl(context);
    }

    private sealed class StubStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "TestEntity";
        public IReadOnlyList<TaskInferenceField> Fields => [new TaskInferenceField("title", "string", "Title")];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class CapturingPort(int[] count, string responseJson) : IInferenceCompletionPort
    {
        public Task<JsonElement> CompleteStructuredAsync(
            InferenceCompletionRequest request, CancellationToken cancellationToken = default)
        {
            System.Threading.Interlocked.Increment(ref count[0]);
            using var doc = JsonDocument.Parse(responseJson);
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    private sealed class ThrowingPort(Exception ex) : IInferenceCompletionPort
    {
        public Task<JsonElement> CompleteStructuredAsync(
            InferenceCompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<JsonElement>(ex);
    }
}
