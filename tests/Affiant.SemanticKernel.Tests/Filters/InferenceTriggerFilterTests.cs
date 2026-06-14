namespace Affiant.SemanticKernel.Tests.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Unit tests for InferenceTriggerFilter.
/// Verifies trigger evaluation, idempotency bookkeeping, strategy resolution,
/// fail-safe behavior, and "next always fires" contract.
/// Tests drive the filter via kernel.InvokeAsync so SK creates a real FunctionInvocationContext.
/// </summary>
public class InferenceTriggerFilterTests
{
    // ── Trigger evaluation ───────────────────────────────────────────────────

    [Fact]
    public async Task AllTriggersReturnFalse_SkipsInference_CallsNext()
    {
        var (filter, invCount, _, kernel) = BuildTestHarness(triggerResult: false);
        kernel.FunctionInvocationFilters.Add(filter);

        await kernel.InvokeAsync("TestPlugin", "WriteFn");

        Assert.Equal(0, invCount[0]);
    }

    [Fact]
    public async Task FirstTriggerReturnsTrue_CallsRunnerOnce()
    {
        var (filter, invCount, registry, kernel) = BuildTestHarness(triggerResult: true, withDescriptor: true);
        kernel.FunctionInvocationFilters.Add(filter);

        kernel.Data["ConversationId"] = "conv1";
        kernel.Data["AffiantTurnNumber"] = 0;
        kernel.Data["ChatHistory"] = new ChatHistory();

        await kernel.InvokeAsync("TestPlugin", "WriteFn");

        Assert.Equal(1, invCount[0]);
    }

    [Fact]
    public async Task MultipleTriggersFirstTrue_RunnerCalledOnce_ShortCircuits()
    {
        int evaluatedCount = 0;
        var triggers = new List<IInferenceTrigger>
        {
            new CountingTrigger(_ => { evaluatedCount++; return true; }),
            new CountingTrigger(_ => { evaluatedCount++; return true; }) // should not be reached
        };

        var (filter, invCount, _, kernel) = BuildTestHarness(triggers: triggers, withDescriptor: true);
        kernel.FunctionInvocationFilters.Add(filter);
        kernel.Data["ConversationId"] = "conv-multi";
        kernel.Data["AffiantTurnNumber"] = 0;
        kernel.Data["ChatHistory"] = new ChatHistory();

        await kernel.InvokeAsync("TestPlugin", "WriteFn");

        Assert.Equal(1, evaluatedCount); // short-circuit: second trigger never evaluated
        Assert.Equal(1, invCount[0]);
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SameConvFunctionTurn_RunnerCalledOnce()
    {
        var (filter, invCount, _, kernel) = BuildTestHarness(triggerResult: true, withDescriptor: true);
        kernel.FunctionInvocationFilters.Add(filter);
        kernel.Data["ConversationId"] = "conv-idem";
        kernel.Data["AffiantTurnNumber"] = 1;
        kernel.Data["ChatHistory"] = new ChatHistory();

        // First invocation
        await kernel.InvokeAsync("TestPlugin", "WriteFn");
        // Second invocation with same (conv, fn, turn) — must be a no-op
        await kernel.InvokeAsync("TestPlugin", "WriteFn");

        Assert.Equal(1, invCount[0]);
    }

    [Fact]
    public async Task SameConvFunction_DifferentTurn_RunnerCalledTwice()
    {
        var (filter, invCount, _, kernel) = BuildTestHarness(triggerResult: true, withDescriptor: true);
        kernel.FunctionInvocationFilters.Add(filter);
        kernel.Data["ConversationId"] = "conv-turn";
        kernel.Data["ChatHistory"] = new ChatHistory();

        // Turn 1
        kernel.Data["AffiantTurnNumber"] = 1;
        await kernel.InvokeAsync("TestPlugin", "WriteFn");

        // Turn 2 — different turn number, inference must fire again
        kernel.Data["AffiantTurnNumber"] = 2;
        await kernel.InvokeAsync("TestPlugin", "WriteFn");

        Assert.Equal(2, invCount[0]);
    }

    // ── Strategy resolution ───────────────────────────────────────────────────

    [Fact]
    public async Task NoDescriptor_LogsWarning_CallsNext_DoesNotThrow()
    {
        // Trigger returns true but no descriptor registered — should log + continue
        var (filter, invCount, _, kernel) = BuildTestHarness(
            triggerResult: true, withDescriptor: false);
        kernel.FunctionInvocationFilters.Add(filter);
        kernel.Data["ConversationId"] = "conv-nodesc";
        kernel.Data["AffiantTurnNumber"] = 0;

        // Should not throw
        var result = await kernel.InvokeAsync("TestPlugin", "WriteFn");

        Assert.Equal("fn-result", result.GetValue<string>());
        Assert.Equal(0, invCount[0]); // runner not called
    }

    // ── Fail-safe ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunnerThrowsNonCancellation_LogsWarning_ContinuesToNextAndReturnsResult()
    {
        var throwingPort = new ThrowingPort(new InvalidOperationException("port failure"));
        var (filter, _, _, kernel) = BuildTestHarness(
            triggerResult: true, withDescriptor: true, port: throwingPort);
        kernel.FunctionInvocationFilters.Add(filter);
        kernel.Data["ConversationId"] = "conv-fail";
        kernel.Data["AffiantTurnNumber"] = 0;
        kernel.Data["ChatHistory"] = new ChatHistory();

        // Must not throw — fail-safe catches and continues
        var result = await kernel.InvokeAsync("TestPlugin", "WriteFn");
        Assert.Equal("fn-result", result.GetValue<string>());
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunnerThrowsCancellation_ExceptionPropagates_NextDoesNotFire()
    {
        // Arrange: port throws OperationCanceledException — simulates cancellation propagating.
        var cancellingPort = new ThrowingPort(new OperationCanceledException());
        var (filter, _, _, kernel) = BuildTestHarness(
            triggerResult: true, withDescriptor: true, port: cancellingPort);
        kernel.FunctionInvocationFilters.Add(filter);
        kernel.Data["ConversationId"] = "conv-cancel";
        kernel.Data["AffiantTurnNumber"] = 0;
        kernel.Data["ChatHistory"] = new ChatHistory();

        // Act + Assert: OCE must propagate; the fail-safe catch block must NOT swallow it.
        // If next(context) fires, kernel.InvokeAsync would return "fn-result" instead of throwing —
        // so an exception here also proves next was not reached.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kernel.InvokeAsync("TestPlugin", "WriteFn"));
    }

    // ── Next always fires ─────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCallAlwaysFires_EvenWhenTriggerReturnsFalse()
    {
        var (filter, _, _, kernel) = BuildTestHarness(triggerResult: false);
        kernel.FunctionInvocationFilters.Add(filter);

        var result = await kernel.InvokeAsync("TestPlugin", "WriteFn");

        Assert.Equal("fn-result", result.GetValue<string>());
    }

    [Fact]
    public async Task ToolCallAlwaysFires_EvenAfterInference()
    {
        var (filter, _, _, kernel) = BuildTestHarness(triggerResult: true, withDescriptor: true);
        kernel.FunctionInvocationFilters.Add(filter);
        kernel.Data["ConversationId"] = "conv-always";
        kernel.Data["AffiantTurnNumber"] = 0;
        kernel.Data["ChatHistory"] = new ChatHistory();

        var result = await kernel.InvokeAsync("TestPlugin", "WriteFn");

        Assert.Equal("fn-result", result.GetValue<string>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (
        InferenceTriggerFilter Filter,
        int[] InvocationCount,
        AffiantToolRegistry Registry,
        Kernel Kernel)
    BuildTestHarness(
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
        var runner = new TaskInferenceRunner(capturePort, fabric, step,
            NullLogger<TaskInferenceRunner>.Instance);

        var registry = new AffiantToolRegistry();

        if (withDescriptor)
            registry.Register(new AffiantToolDescriptor(
                "WriteFn", "TestPlugin",
                new Operation("WriteCreate"), "TestEntity", typeof(StubStrategy)));

        // Service provider exposes IContextFabric (for the filter's DI resolution).
        // StubStrategy is registered both as ITaskInferenceStrategy AND as its concrete type,
        // because InferenceTriggerFilter calls GetRequiredService(descriptor.InferenceStrategy)
        // where InferenceStrategy is typeof(StubStrategy) — the concrete type key.
        var strategy = new StubStrategy();
        var services = new ServiceCollection();
        services.AddSingleton<IContextFabric>(fabric);
        services.AddSingleton<ITaskInferenceStrategy>(strategy);
        services.AddSingleton<StubStrategy>(strategy);
        var sp = services.BuildServiceProvider();

        var triggerList = triggers ?? new IInferenceTrigger[]
        {
            new FakeTrigger(_ => triggerResult)
        };

        var filter = new InferenceTriggerFilter(
            triggerList, runner, sp, registry,
            NullLogger<InferenceTriggerFilter>.Instance);

        // Build kernel with the test plugin
        var kernel = Kernel.CreateBuilder().Build();
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("TestPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => "fn-result", "WriteFn")]));

        return (filter, invCount, registry, kernel);
    }

    private sealed class FakeTrigger : IInferenceTrigger
    {
        private readonly Func<InferenceTriggerContext, bool> _impl;
        public FakeTrigger(Func<InferenceTriggerContext, bool> impl) => _impl = impl;
        public bool ShouldRun(InferenceTriggerContext context) => _impl(context);
    }

    private sealed class CountingTrigger : IInferenceTrigger
    {
        private readonly Func<InferenceTriggerContext, bool> _impl;
        public CountingTrigger(Func<InferenceTriggerContext, bool> impl) => _impl = impl;
        public bool ShouldRun(InferenceTriggerContext context) => _impl(context);
    }

    private sealed class StubStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "TestEntity";
        public IReadOnlyList<TaskInferenceField> Fields =>
            [new TaskInferenceField("title", "string", "Title")];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class CapturingPort : IInferenceCompletionPort
    {
        private readonly int[] _count;
        private readonly string _responseJson;
        public CapturingPort(int[] count, string responseJson)
        {
            _count = count;
            _responseJson = responseJson;
        }
        public Task<JsonElement> CompleteStructuredAsync(
            InferenceCompletionRequest request, CancellationToken cancellationToken = default)
        {
            System.Threading.Interlocked.Increment(ref _count[0]);
            using var doc = JsonDocument.Parse(_responseJson);
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    private sealed class ThrowingPort : IInferenceCompletionPort
    {
        private readonly Exception _ex;
        public ThrowingPort(Exception ex) => _ex = ex;
        public Task<JsonElement> CompleteStructuredAsync(
            InferenceCompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<JsonElement>(_ex);
    }
}
