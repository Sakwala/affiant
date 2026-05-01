namespace Affiant.SemanticKernel.Filters;

using Affiant.Core.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

/// <summary>
/// Registration helper for the Affiant Semantic Kernel filter pipeline.
/// Consumed by <c>AddAffiantSemanticKernel()</c>.
///
/// Full pipeline execution order (non-negotiable per framework spec §6):
///   1. ToolErrorFilter           — IFunctionInvocationFilter; registered by AddAffiantCore()
///   2. DeterministicShortCircuit — IFunctionInvocationFilter; registered by AddAffiantCore()
///   3. ContextExtractor subclasses — IFunctionInvocationFilter; host-registered domain extractors
///   4. TaskInferenceFilter       — IAutoFunctionInvocationFilter; registered by AddAffiantSkFilters()
///   5. ReviewGateFilter          — IAutoFunctionInvocationFilter; registered by AddAffiantSkFilters()
///
/// Positions 1 and 2 are satisfied by calling <c>AddAffiantCore()</c> before this helper.
/// Position 3 is satisfied by host apps registering their ContextExtractor subclasses.
/// This helper satisfies positions 4 and 5.
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
        // Position 4: Task inference — fires after each LLM auto-invoked function.
        // Merges structured output with field/confidence pairs into ContextFabric
        // using TaskInferenceStep's confidence-based merge rule (framework spec §2.3).
        // Scoped lifetime required: TaskInferenceStep captures ContextFabric (Scoped in most hosts).
        services.AddScoped<IAutoFunctionInvocationFilter, TaskInferenceFilter>();

        // Position 5: ReviewGate adapter — detects WriteProposal results and routes them through
        // ReviewGate.FileReviewAsync using a fresh per-invocation scope. No-op if
        // IReviewContextProvider or ReviewGate are not registered in the DI container.
        services.AddScoped<IAutoFunctionInvocationFilter, ReviewGateFilter>();

        return services;
    }
}
