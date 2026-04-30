namespace Affiant.Core.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Affiant.Core.Policies;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.SemanticKernel;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> to register
/// Affiant.Core framework services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Affiant.Core framework services.
    /// Uses <c>TryAdd</c> semantics so host-registered services take precedence
    /// (register your overrides before calling this method).
    /// </summary>
    /// <remarks>
    /// The following services are registered as Singletons by this method:
    /// <list type="bullet">
    /// <item><c>ContextFabric</c> — entity state tracking</item>
    /// <item><c>TaskInferenceStep</c> — confidence-based merge logic (requires <c>ITaskInferenceStrategy</c>)</item>
    /// <item><c>ApprovalPolicyEvaluator</c> / <c>IApprovalPolicyEvaluator</c> — policy pipeline</item>
    /// <item><c>ReviewerConfirmationPolicy</c> as <c>IApprovalPolicy</c> — default policy</item>
    /// <item><c>DeterministicShortCircuit</c> as <c>IFunctionInvocationFilter</c> — pre-LLM interception</item>
    /// <item><c>ToolErrorFilter</c> as <c>IFunctionInvocationFilter</c> — error handling with retry</item>
    /// </list>
    /// Services whose lifetimes depend on host-scoped adapter registrations
    /// (<c>ReviewGate</c>, <c>SessionRehydrator</c>, <c>TaskInferenceFilter</c>, <c>UiGuidanceBridge</c>)
    /// are intentionally omitted and must be registered directly by the host with the appropriate lifetime.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddAffiantCore(options =>
    /// {
    ///     options.PrimaryProvider = "AzureOpenAI";
    ///     options.FallbackProvider = "Gemini";
    ///     options.DefaultDocketTtl = TimeSpan.FromMinutes(10);
    ///     options.EnableObservability = true;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddAffiantCore(
        this IServiceCollection services,
        Action<AffiantCoreOptions>? configure = null)
    {
        var options = new AffiantCoreOptions();
        configure?.Invoke(options);

        // Step 1: Entity state tracking
        services.TryAddSingleton<ContextFabric>();

        // Step 3: Structured-output merge logic
        services.TryAddSingleton<TaskInferenceStep>();

        // Step 5: Policy evaluation pipeline
        services.TryAddSingleton<ApprovalPolicyEvaluator>();
        services.TryAddSingleton<IApprovalPolicyEvaluator>(
            sp => sp.GetRequiredService<ApprovalPolicyEvaluator>());

        // Step 7: Default approval policy
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApprovalPolicy, ReviewerConfirmationPolicy>());

        // Step 8: Pre-LLM intent interception
        services.TryAddSingleton<DeterministicShortCircuit>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFunctionInvocationFilter, DeterministicShortCircuit>());

        // Step 9: Error-handling filter
        services.TryAddSingleton<ToolErrorFilter>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFunctionInvocationFilter, ToolErrorFilter>());

        // Step 13: Telemetry infrastructure (idempotent static initialisation)
        if (options.EnableObservability)
        {
            _ = AffiantTelemetry.AffiantActivitySource;
            _ = AffiantTelemetry.AffiantMeter;
        }

        services.AddSingleton(options);

        return services;
    }
}
