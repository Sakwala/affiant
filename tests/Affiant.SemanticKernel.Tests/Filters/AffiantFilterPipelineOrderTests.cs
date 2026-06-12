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
/// Asserts the Affiant filter pipeline registration order per L2 PRD §"Task 4".
/// Pipeline order (non-negotiable per framework spec §6):
///   Pre-tool  (IFunctionInvocationFilter):
///     1. ToolErrorFilter, 2. DeterministicShortCircuit, 3. ContextExtractor*,
///     4. ToolArgumentCaptureFilter, 5. InferenceTriggerFilter
///   Post-tool (IAutoFunctionInvocationFilter):
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
        // Stub-register strategy + descriptor so the pre-tool filters can resolve their deps:
        services.AddSingleton<ITaskInferenceStrategy>(new FakeStrategy());
        services.AddAffiantTool<FakeStrategy>("CreateThing", Operation.WriteCreate, "Thing");
        return services.BuildServiceProvider();
    }

    [Fact]
    public void PreToolFilters_RegisteredInExpectedOrder()
    {
        var provider = BuildPipeline();
        var filters = provider.GetServices<IFunctionInvocationFilter>().ToArray();

        // ToolErrorFilter, DeterministicShortCircuit, ToolTracingFilter (AddAffiantCore),
        // ToolArgumentCaptureFilter, InferenceTriggerFilter (AddAffiantInferenceOrchestration).
        // No host-registered ContextExtractor in this stub.
        var toolErrorIdx = Array.FindIndex(filters, f => f is ToolErrorFilter);
        var shortCircuitIdx = Array.FindIndex(filters, f => f is DeterministicShortCircuit);
        var toolArgCaptureIdx = Array.FindIndex(filters, f => f is ToolArgumentCaptureFilter);
        var inferTriggerIdx = Array.FindIndex(filters, f => f is InferenceTriggerFilter);

        Assert.True(toolErrorIdx >= 0, "ToolErrorFilter must be registered");
        Assert.True(shortCircuitIdx >= 0, "DeterministicShortCircuit must be registered");
        Assert.True(toolArgCaptureIdx >= 0, "ToolArgumentCaptureFilter must be registered");
        Assert.True(inferTriggerIdx >= 0, "InferenceTriggerFilter must be registered");

        // ToolArgumentCaptureFilter and InferenceTriggerFilter must come AFTER
        // the AddAffiantCore filters (ToolErrorFilter, DeterministicShortCircuit).
        Assert.True(toolErrorIdx < toolArgCaptureIdx,
            $"ToolErrorFilter (idx {toolErrorIdx}) must precede ToolArgumentCaptureFilter (idx {toolArgCaptureIdx})");
        Assert.True(shortCircuitIdx < toolArgCaptureIdx,
            $"DeterministicShortCircuit (idx {shortCircuitIdx}) must precede ToolArgumentCaptureFilter (idx {toolArgCaptureIdx})");
        Assert.True(toolArgCaptureIdx < inferTriggerIdx,
            $"ToolArgumentCaptureFilter (idx {toolArgCaptureIdx}) must precede InferenceTriggerFilter (idx {inferTriggerIdx})");
    }

    [Fact]
    public void PostToolFilters_RegisteredInExpectedOrder()
    {
        var provider = BuildPipeline();
        var filters = provider.GetServices<IAutoFunctionInvocationFilter>().ToArray();

        // TaskInferenceMergeFilter (was TaskInferenceFilter), ReviewGateFilter
        Assert.Equal(2, filters.Length);
        Assert.IsType<TaskInferenceMergeFilter>(filters[0]);
        Assert.IsType<ReviewGateFilter>(filters[1]);
    }

    [Fact]
    public void HostRegisteredContextExtractor_RunsBeforePreToolL2Filters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore();
        services.AddSingleton<IFunctionInvocationFilter, FakeContextExtractor>();
        services.AddAffiantInferenceOrchestration();
        services.AddAffiantSkFilters();
        services.AddSingleton<ITaskInferenceStrategy>(new FakeStrategy());
        services.AddAffiantTool<FakeStrategy>("CreateThing", Operation.WriteCreate, "Thing");

        var filters = services.BuildServiceProvider().GetServices<IFunctionInvocationFilter>().ToArray();
        // Order: ToolError, ShortCircuit, (ToolTracing), ContextExtractor (host), ToolArgCapture, InferenceTrigger
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

    private sealed class FakeContextExtractor : IFunctionInvocationFilter
    {
        public Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next) => next(context);
    }
}
