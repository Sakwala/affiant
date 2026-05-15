namespace Affiant.Core.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
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
    /// <item><c>DeterministicShortCircuit</c> as <c>IFunctionInvocationFilter</c> — pre-LLM interception</item>
    /// <item><c>ToolErrorFilter</c> as <c>IFunctionInvocationFilter</c> — error handling with retry</item>
    /// <item><c>ToolTracingFilter</c> as <c>IFunctionInvocationFilter</c> — per-tool <c>execute_tool</c> OTel span</item>
    /// </list>
    /// No <c>IApprovalPolicy</c> is registered by default. Hosts must call
    /// <c>AddAffiantPolicies()</c> from Affiant.Policies to declare their policy graph.
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

        // Tool descriptor registry — always present once framework DI is added
        services.TryAddSingleton<IAffiantToolRegistry, AffiantToolRegistry>();

        // Step 1: Entity state tracking
        services.TryAddSingleton<ContextFabric>();

        // Step 3: Structured-output merge logic
        services.TryAddSingleton<TaskInferenceStep>();

        // Step 5: Policy evaluation pipeline
        services.TryAddSingleton<ApprovalPolicyEvaluator>();
        services.TryAddSingleton<IApprovalPolicyEvaluator>(
            sp => sp.GetRequiredService<ApprovalPolicyEvaluator>());

        // No default IApprovalPolicy registered here — hosts declare their policy graph
        // via AddAffiantPolicies() in Affiant.Policies. The evaluator's built-in fallback
        // returns ReviewerConfirmation when no policy matches.

        // Step 8: Pre-LLM intent interception
        services.TryAddSingleton<DeterministicShortCircuit>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFunctionInvocationFilter, DeterministicShortCircuit>());

        // Step 9: Error-handling filter (outermost in inner pipeline — wraps ToolTracingFilter)
        services.TryAddSingleton<ToolErrorFilter>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFunctionInvocationFilter, ToolErrorFilter>());

        // Step 10: Per-tool OTel span — creates execute_tool span for all hosts automatically
        services.TryAddSingleton<ToolTracingFilter>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFunctionInvocationFilter, ToolTracingFilter>());

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
