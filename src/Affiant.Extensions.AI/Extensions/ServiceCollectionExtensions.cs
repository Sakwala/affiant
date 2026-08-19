namespace Affiant.Extensions.AI.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.Core.Triggers;
using Affiant.Extensions.AI.Adapters;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI extension for the Affiant.Extensions.AI adapter.
///
/// <para>
/// Microsoft.Extensions.AI exposes one function-calling seam (unlike Semantic Kernel's
/// invocation/auto-invocation split), so this single call registers every neutral filter position
/// 4–7 that <c>Affiant.SemanticKernel</c> splits across <c>AddAffiantInferenceOrchestration</c> and
/// <c>AddAffiantSkFilters</c>: <c>ToolArgumentCaptureFilter</c>, <c>InferenceTriggerFilter</c>,
/// <c>TaskInferenceMergeFilter</c>, <c>ReviewGateFilter</c> — plus this adapter's inference port and
/// task-inference orchestration services. Positions 1–3 (<c>ToolErrorFilter</c>,
/// <c>DeterministicShortCircuit</c>, <c>ToolTracingFilter</c>, and host <c>ContextExtractor</c>
/// subclasses) come from <c>AddAffiantCore()</c>, which must be called first — that call also
/// installs <c>AffiantWireUpValidator</c>, the startup check that fails the host when the review
/// loop's transport/docket dependencies were never registered.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Microsoft.Extensions.AI adapter's services and neutral filter positions 4–7.
    /// Call after <c>services.AddAffiantCore()</c>.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="ExtensionsAIOptions"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddAffiantExtensionsAI(
        this IServiceCollection services,
        Action<ExtensionsAIOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ExtensionsAIOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Port: IInferenceCompletionPort over the host's IChatClient, with no ChatOptions.Tools so
        // the inference call cannot recurse through function invocation. Host registers IChatClient.
        services.TryAddScoped<IInferenceCompletionPort, ExtensionsAIInferenceCompletionPort>();

        services.TryAddScoped<TaskInferenceRunner>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IInferenceTrigger, WriteIntentInferenceTrigger>());

        if (!services.Any(sd => sd.ServiceType == typeof(IAffidavitProjection)))
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Scoped<IAffidavitProjection, SchemaDrivenAffidavitProjection>());
        }

        // Neutral filter positions 4–7 (canonical order, framework spec §3.12.4). This adapter has
        // no stage split, so all four run at the one seam AffiantDelegatingAIFunction fires —
        // registration order here fixes their position in the pipeline. The invocation-stage pair
        // (positions 4, 5) registers here; the completion-stage pair (positions 6, 7) registers via
        // the shared Core helper so the merge-before-review onion order matches the other bridges.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IToolInvocationFilter, ToolArgumentCaptureFilter>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IToolInvocationFilter, InferenceTriggerFilter>());
        services.AddAffiantCompletionFilters();

        return services;
    }
}
