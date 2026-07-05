namespace Affiant.SemanticKernel.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.Core.Triggers;
using Affiant.SemanticKernel.Adapters;
using Affiant.SemanticKernel.Extensions;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Xunit;

/// <summary>
/// Tests that AddAffiantInferenceOrchestration() correctly wires the full L2 stack
/// into the DI container when combined with AddAffiantCore().
/// Order assertions are NOT made here — that is Story 16.4's job.
/// </summary>
public class AddAffiantInferenceOrchestrationTests
{
    // ── Service resolution ────────────────────────────────────────────────────

    [Fact]
    public void AddAffiantInferenceOrchestration_InferenceCompletionPort_ResolvesToSkPort()
    {
        using var scope = BuildScope();
        var port = scope.ServiceProvider.GetRequiredService<IInferenceCompletionPort>();
        Assert.IsType<SemanticKernelInferenceCompletionPort>(port);
    }

    [Fact]
    public void AddAffiantInferenceOrchestration_TaskInferenceRunner_Resolves()
    {
        using var scope = BuildScope();
        var runner = scope.ServiceProvider.GetRequiredService<TaskInferenceRunner>();
        Assert.NotNull(runner);
    }

    [Fact]
    public void AddAffiantInferenceOrchestration_InferenceTriggerEnumerable_ContainsWriteIntentTrigger()
    {
        using var scope = BuildScope();
        var triggers = scope.ServiceProvider.GetServices<IInferenceTrigger>().ToList();
        Assert.Contains(triggers, t => t is WriteIntentInferenceTrigger);
    }

    [Fact]
    public void AddAffiantInferenceOrchestration_AffidavitProjectionEnumerable_ContainsSchemaDriven()
    {
        using var scope = BuildScope();
        var projections = scope.ServiceProvider.GetServices<IAffidavitProjection>().ToList();
        Assert.Contains(projections, p => p is SchemaDrivenAffidavitProjection);
    }

    [Fact]
    public void AddAffiantInferenceOrchestration_ToolInvocationFilterEnumerable_ContainsBothFilters()
    {
        using var scope = BuildScope();
        var filters = scope.ServiceProvider.GetServices<IToolInvocationFilter>().ToList();

        Assert.Contains(filters, f => f is ToolArgumentCaptureFilter);
        Assert.Contains(filters, f => f is InferenceTriggerFilter);
        // Order assertion is asserted by the pipeline-order tests — assert only membership here.
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public void AddAffiantInferenceOrchestration_CalledTwice_ProducesSameDiShape()
    {
        var services = BuildBaseServices();
        services.AddAffiantInferenceOrchestration();
        services.AddAffiantInferenceOrchestration(); // second call must be a no-op

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        // IInferenceCompletionPort: TryAddScoped ensures only one registration
        var ports = scope.ServiceProvider.GetServices<IInferenceCompletionPort>().ToList();
        Assert.Single(ports);

        // IToolInvocationFilter: TryAddEnumerable deduplicates by (ServiceType, ImplementationType)
        var filters = scope.ServiceProvider.GetServices<IToolInvocationFilter>().ToList();
        var captureCount = filters.Count(f => f is ToolArgumentCaptureFilter);
        var triggerCount = filters.Count(f => f is InferenceTriggerFilter);
        Assert.Equal(1, captureCount);
        Assert.Equal(1, triggerCount);

        // IInferenceTrigger: TryAddEnumerable deduplicates WriteIntentInferenceTrigger
        var triggers = scope.ServiceProvider.GetServices<IInferenceTrigger>().ToList();
        var writeTriggerCount = triggers.Count(t => t is WriteIntentInferenceTrigger);
        Assert.Equal(1, writeTriggerCount);
    }

    // ── Integration: kernel resolves correctly ────────────────────────────────

    [Fact]
    public void AddAffiantInferenceOrchestration_KernelResolvesWithBridge()
    {
        var services = BuildBaseServices();
        services.AddAffiantInferenceOrchestration();
        services.AddAffiantSkFilters();
        services.AddKernel();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        // Kernel must resolve without errors in this DI configuration
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();
        Assert.NotNull(kernel);

        // The SK function-invocation bridge is the only IFunctionInvocationFilter the kernel sees;
        // it runs the neutral pre-tool filters internally.
        Assert.Contains(kernel.FunctionInvocationFilters, f => f is AffiantFunctionInvocationBridge);
        Assert.Contains(kernel.AutoFunctionInvocationFilters, f => f is AffiantAutoFunctionInvocationBridge);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IServiceScope BuildScope()
    {
        var services = BuildBaseServices();
        services.AddAffiantInferenceOrchestration();
        var sp = services.BuildServiceProvider();
        return sp.CreateScope();
    }

    private static ServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKernel();
        services.AddSingleton<ITaskInferenceStrategy, StubStrategy>();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        return services;
    }

    private sealed class StubStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "StubEntity";
        public IReadOnlyList<TaskInferenceField> Fields =>
            [new TaskInferenceField("title", "string", "Title of the item")];
        public double? MinimumConfidenceThreshold => null;
    }
}
