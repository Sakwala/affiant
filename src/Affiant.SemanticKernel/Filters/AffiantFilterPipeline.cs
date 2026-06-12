namespace Affiant.SemanticKernel.Filters;

using Affiant.Core.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

/// <summary>
/// Registration helper for the Affiant Semantic Kernel filter pipeline.
/// Consumed by <c>AddAffiantSemanticKernel()</c>.
///
/// Full pipeline execution order (non-negotiable per framework spec §6 and L2 PRD §"Task 4"):
///
///  Pre-tool (IFunctionInvocationFilter) — runs in DI registration order
///    before the function executes:
///    1. ToolErrorFilter                  — AddAffiantCore() (Step 9)
///    2. DeterministicShortCircuit         — AddAffiantCore() (Step 8)
///    3. ContextExtractor* subclasses      — host-registered, post-tool ctx extractors
///    4. ToolArgumentCaptureFilter         — AddAffiantInferenceOrchestration() (Story 16.3)
///    5. InferenceTriggerFilter            — AddAffiantInferenceOrchestration() (Story 16.3)
///
///  Post-tool (IAutoFunctionInvocationFilter) — runs in DI registration order
///    after the function returns:
///    6. TaskInferenceMergeFilter          — this helper (was TaskInferenceFilter pre-16.4)
///    7. ReviewGateFilter                  — this helper
///
/// Positions 1, 2 are satisfied by calling AddAffiantCore() before this helper.
/// Position 3 is satisfied by host apps registering their ContextExtractor subclasses.
/// Positions 4, 5 are satisfied by hosts calling AddAffiantInferenceOrchestration()
///   (from Story 16.3) before this helper.
/// Positions 6, 7 are satisfied by this helper.
/// </summary>
public static class AffiantFilterPipeline
{
    /// <summary>
    /// Registers the Affiant Semantic Kernel auto-function invocation filters.
    /// <c>AddAffiantCore()</c> must be called first — it registers ToolErrorFilter and
    /// DeterministicShortCircuit which form the outer envelope of this pipeline.
    /// </summary>
    public static IServiceCollection AddAffiantSkFilters(this IServiceCollection services)
    {
        // Position 6: TaskInferenceMergeFilter (was TaskInferenceFilter pre-16.4) — fires after
        // each LLM auto-invoked function. Merges structured output with field/confidence pairs
        // into ContextFabric using TaskInferenceStep's confidence-based merge rule (framework
        // spec §2.3). Pre-tool inference (positions 4, 5) is registered separately by
        // AddAffiantInferenceOrchestration (Story 16.3).
        // Scoped lifetime required: TaskInferenceStep captures ContextFabric (Scoped in most hosts).
        services.AddScoped<IAutoFunctionInvocationFilter, TaskInferenceMergeFilter>();

        // Position 5: ReviewGate adapter — detects WriteProposal results and routes them through
        // ReviewGate.FileReviewAsync using a fresh per-invocation scope. No-op if
        // IReviewContextProvider or ReviewGate are not registered in the DI container.
        services.AddScoped<IAutoFunctionInvocationFilter, ReviewGateFilter>();

        return services;
    }
}
