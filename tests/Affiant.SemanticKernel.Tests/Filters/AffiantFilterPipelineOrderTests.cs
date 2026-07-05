namespace Affiant.SemanticKernel.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Extensions;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Xunit;

/// <summary>
/// Asserts that the Semantic Kernel wiring registers the neutral filters in canonical order
/// (framework spec §3.12.4) and exposes exactly the two bridges to the kernel:
///   Invocation stage (SK IFunctionInvocationFilter → AffiantFunctionInvocationBridge):
///     1. ToolErrorFilter, 2. DeterministicShortCircuit, (ToolTracingFilter),
///     3. ContextExtractor*, 4. ToolArgumentCaptureFilter, 5. InferenceTriggerFilter
///   Completion stage (SK IAutoFunctionInvocationFilter → AffiantAutoFunctionInvocationBridge):
///     6. TaskInferenceMergeFilter, 7. ReviewGateFilter
/// </summary>
public class AffiantFilterPipelineOrderTests
{
    private static IServiceProvider BuildPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Reproduce the host-side call sequence:
        services.AddAffiantCore();
        services.AddAffiantInferenceOrchestration();
        services.AddAffiantSkFilters();
        services.AddSingleton<ITaskInferenceStrategy>(new FakeStrategy());
        services.AddAffiantTool<FakeStrategy>("CreateThing", Operation.WriteCreate, "Thing");
        return services.BuildServiceProvider();
    }

    [Fact]
    public void NeutralFilters_RegisteredInCanonicalOrder()
    {
        var provider = BuildPipeline();
        var filters = provider.GetServices<IToolInvocationFilter>().ToArray();

        var toolErrorIdx = Array.FindIndex(filters, f => f is ToolErrorFilter);
        var shortCircuitIdx = Array.FindIndex(filters, f => f is DeterministicShortCircuit);
        var toolArgCaptureIdx = Array.FindIndex(filters, f => f is ToolArgumentCaptureFilter);
        var inferTriggerIdx = Array.FindIndex(filters, f => f is InferenceTriggerFilter);
        var mergeIdx = Array.FindIndex(filters, f => f is TaskInferenceMergeFilter);
        var reviewIdx = Array.FindIndex(filters, f => f is ReviewGateFilter);

        Assert.True(toolErrorIdx >= 0, "ToolErrorFilter must be registered");
        Assert.True(shortCircuitIdx >= 0, "DeterministicShortCircuit must be registered");
        Assert.True(toolArgCaptureIdx >= 0, "ToolArgumentCaptureFilter must be registered");
        Assert.True(inferTriggerIdx >= 0, "InferenceTriggerFilter must be registered");

        Assert.True(toolErrorIdx < toolArgCaptureIdx);
        Assert.True(shortCircuitIdx < toolArgCaptureIdx);
        Assert.True(toolArgCaptureIdx < inferTriggerIdx);
        // Completion filters follow the pre-tool filters and are ordered merge-before-review.
        Assert.True(inferTriggerIdx < mergeIdx);
        Assert.True(mergeIdx < reviewIdx);
    }

    [Fact]
    public void Bridges_AreTheOnlySkFilters()
    {
        var provider = BuildPipeline();

        var fnFilters = provider.GetServices<IFunctionInvocationFilter>().ToArray();
        Assert.Single(fnFilters);
        Assert.IsType<AffiantFunctionInvocationBridge>(fnFilters[0]);

        var autoFilters = provider.GetServices<IAutoFunctionInvocationFilter>().ToArray();
        Assert.Single(autoFilters);
        Assert.IsType<AffiantAutoFunctionInvocationBridge>(autoFilters[0]);
    }

    [Fact]
    public void HostRegisteredContextExtractor_RunsBeforePreToolL2Filters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore();
        services.AddSingleton<IToolInvocationFilter, FakeContextExtractor>();
        services.AddAffiantInferenceOrchestration();
        services.AddAffiantSkFilters();
        services.AddSingleton<ITaskInferenceStrategy>(new FakeStrategy());
        services.AddAffiantTool<FakeStrategy>("CreateThing", Operation.WriteCreate, "Thing");

        var filters = services.BuildServiceProvider().GetServices<IToolInvocationFilter>().ToArray();
        var extractorIdx = Array.FindIndex(filters, f => f is FakeContextExtractor);
        var toolArgIdx = Array.FindIndex(filters, f => f is ToolArgumentCaptureFilter);
        var inferTriggerIdx = Array.FindIndex(filters, f => f is InferenceTriggerFilter);
        Assert.True(extractorIdx < toolArgIdx,
            $"ContextExtractor (idx {extractorIdx}) must run BEFORE ToolArgumentCaptureFilter (idx {toolArgIdx})");
        Assert.True(toolArgIdx < inferTriggerIdx,
            $"ToolArgumentCaptureFilter (idx {toolArgIdx}) must run BEFORE InferenceTriggerFilter (idx {inferTriggerIdx})");
    }

    [Fact]
    public void Rename_TaskInferenceFilter_Removed()
    {
        // Negative-path: the old type must no longer exist in Affiant.Core.Filters.
        var assembly = typeof(TaskInferenceMergeFilter).Assembly;
        var oldType = assembly.GetType("Affiant.Core.Filters.TaskInferenceFilter");
        Assert.Null(oldType);  // Pre-1.0 rename: no [Obsolete] alias per packages/CLAUDE.md
    }

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";
        public IReadOnlyList<TaskInferenceField> Fields => Array.Empty<TaskInferenceField>();
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class FakeContextExtractor : IToolInvocationFilter
    {
        public Task OnToolInvocationAsync(
            ToolInvocationContext context,
            Func<ToolInvocationContext, Task> next,
            CancellationToken cancellationToken = default) => next(context);
    }
}
