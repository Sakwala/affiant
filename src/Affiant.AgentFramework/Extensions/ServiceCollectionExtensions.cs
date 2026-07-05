namespace Affiant.AgentFramework.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.AgentFramework.Adapters;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.Core.Triggers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI extension for the Affiant.AgentFramework adapter.
///
/// MAF exposes one function-calling seam (unlike Semantic Kernel's invocation/auto-invocation
/// split), so this single call registers every neutral filter position 4–7 that
/// <c>Affiant.SemanticKernel</c> splits across <c>AddAffiantInferenceOrchestration</c> and
/// <c>AddAffiantSkFilters</c>: <c>ToolArgumentCaptureFilter</c>, <c>InferenceTriggerFilter</c>,
/// <c>TaskInferenceMergeFilter</c>, <c>ReviewGateFilter</c> — plus the MAF inference port and
/// task-inference orchestration services. Positions 1–3 (<c>ToolErrorFilter</c>,
/// <c>DeterministicShortCircuit</c>, <c>ToolTracingFilter</c>, and host <c>ContextExtractor</c>
/// subclasses) come from <c>AddAffiantCore()</c>, which must be called first.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAffiantAgentFramework(
        this IServiceCollection services,
        Action<AgentFrameworkOptions>? configure = null)
    {
        var options = new AgentFrameworkOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Port: MAF-side IInferenceCompletionPort — wraps IChatClient with no ChatOptions.Tools so
        // the inference call cannot recurse through function invocation. Host registers IChatClient.
        services.TryAddScoped<IInferenceCompletionPort, AgentFrameworkInferenceCompletionPort>();

        services.TryAddScoped<TaskInferenceRunner>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IInferenceTrigger, WriteIntentInferenceTrigger>());

        if (!services.Any(sd => sd.ServiceType == typeof(IAffidavitProjection)))
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Scoped<IAffidavitProjection, SchemaDrivenAffidavitProjection>());
        }

        // Neutral filter positions 4–7 (canonical order, framework spec §3.12.4). MAF has no
        // stage split, so all four run at the one middleware seam AffiantFunctionInvocationMiddleware
        // fires — registration order here fixes their position in the pipeline.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IToolInvocationFilter, ToolArgumentCaptureFilter>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IToolInvocationFilter, InferenceTriggerFilter>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IToolInvocationFilter, TaskInferenceMergeFilter>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IToolInvocationFilter, ReviewGateFilter>());

        return services;
    }
}
