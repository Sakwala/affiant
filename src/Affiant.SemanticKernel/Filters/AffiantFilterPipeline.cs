namespace Affiant.SemanticKernel.Filters;

using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.SemanticKernel;

/// <summary>
/// Registration helper that wires the Semantic Kernel bridges over the backend-neutral
/// tool-invocation pipeline. Consumed by <c>AddAffiantSemanticKernel()</c>.
///
/// Two bridges translate SK's two interception seams into the one neutral pipeline, preserving
/// today's firing positions and the canonical 7-step order (framework spec §3.12.4):
///
///  Invocation stage — <see cref="AffiantFunctionInvocationBridge"/> (SK IFunctionInvocationFilter),
///    fires on every invocation including manual <c>kernel.InvokeAsync</c>:
///    1. ToolErrorFilter, 2. DeterministicShortCircuit, (ToolTracingFilter),
///    3. ContextExtractor* (host-registered), 4. ToolArgumentCaptureFilter, 5. InferenceTriggerFilter
///
///  Completion stage — <see cref="AffiantAutoFunctionInvocationBridge"/> (SK
///    IAutoFunctionInvocationFilter), fires at the auto-invocation loop where result replacement
///    and termination live:
///    6. TaskInferenceMergeFilter, 7. ReviewGateFilter
///
/// The neutral filters at positions 1–5 are registered by <c>AddAffiantCore()</c> (1, 2, tracing)
/// and <c>AddAffiantInferenceOrchestration()</c> (4, 5); host <c>ContextExtractor</c> subclasses
/// supply position 3. This helper registers the two completion-stage filters (6, 7) plus the two
/// bridges that run them.
/// </summary>
public static class AffiantFilterPipeline
{
    /// <summary>
    /// Registers the Semantic Kernel bridges and the completion-stage neutral filters.
    /// <c>AddAffiantCore()</c> must be called first — it registers the invocation-stage filters
    /// and the <c>ToolInvocationPipeline</c> the bridges run.
    /// </summary>
    public static IServiceCollection AddAffiantSkFilters(this IServiceCollection services)
    {
        // Completion-stage neutral filters (spec steps 6 and 7). Registration order is fixed by the
        // shared Core helper: ReviewGateFilter outermost, TaskInferenceMergeFilter innermost, so that
        // on the onion unwind the merge's post-work completes before the review is filed (§3.12.4).
        services.AddAffiantCompletionFilters();

        // SK bridges — the only IFunctionInvocationFilter / IAutoFunctionInvocationFilter the
        // kernel sees. Each translates its SK context into the neutral pipeline and back.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFunctionInvocationFilter, AffiantFunctionInvocationBridge>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAutoFunctionInvocationFilter, AffiantAutoFunctionInvocationBridge>());

        return services;
    }
}
