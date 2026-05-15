namespace Affiant.SemanticKernel.Extensions;

using Affiant.Core.Services;
using Affiant.SemanticKernel.Connectors;
using Affiant.SemanticKernel.Filters;
using Affiant.SemanticKernel.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

/// <summary>
/// DI extension for the Affiant.SemanticKernel adapter.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the complete Affiant Semantic Kernel adapter.
    ///
    /// Registers in dependency order:
    /// <list type="number">
    /// <item>
    ///   <c>SemanticKernelOptions</c> — framework-level SK configuration singleton.
    /// </item>
    /// <item>
    ///   SK auto-function invocation filter pipeline — positions 4 and 5 per framework spec §6.
    ///   (Positions 1–2 are <c>ToolErrorFilter</c> and <c>DeterministicShortCircuit</c>
    ///   registered by <c>AddAffiantCore()</c>. Position 3 is host-provided
    ///   <c>ContextExtractor</c> subclasses. Position 4 is <c>TaskInferenceFilter</c>;
    ///   position 5 is <c>ReviewGateFilter</c> — both registered here.)
    /// </item>
    /// <item>
    ///   <c>CapabilityRegistry</c> — resolves <c>IConnectorCapabilities</c> by provider name.
    /// </item>
    /// <item>
    ///   <c>IManualToolInvoker</c> / <c>ManualToolInvoker</c> — fallback invocation path
    ///   for providers that do not support SK's native auto-function invocation.
    /// </item>
    /// </list>
    ///
    /// Call <c>AddAffiantCore()</c> before this method to register the outer pipeline envelope.
    /// Domain-specific <c>ContextExtractor</c> subclasses must be registered separately by the host.
    /// To enable write-proposal review routing, also register <c>IReviewContextProvider</c>
    /// and the full <c>ReviewGate</c> infrastructure (<c>IDocketStore</c>, <c>IStreamingTransport</c>).
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configure">
    /// Optional callback to customize <see cref="SemanticKernelOptions"/>.
    /// If null, all option defaults apply.
    /// </param>
    /// <returns>The <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddAffiantSemanticKernel(
        this IServiceCollection services,
        Action<SemanticKernelOptions>? configure = null)
    {
        // Insert at the front so the validator runs before any host-registered IHostedService.
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, AffiantStartupValidator>());

        var options = new SemanticKernelOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Filter pipeline: positions 4 and 5 per framework spec §6
        // (TaskInferenceFilter + ReviewGateFilter). See AffiantFilterPipeline for full order.
        services.AddAffiantSkFilters();

        // Connector capability registry — maps provider names to IConnectorCapabilities.
        // TryAdd so a host-registered instance takes precedence (e.g., for testing).
        services.TryAddSingleton<CapabilityRegistry>();

        // Manual tool invocation fallback — fires the full IFunctionInvocationFilter chain
        // identically to SK's auto-invocation path, enabling provider-agnostic filter coverage.
        // Scoped because Kernel (and its scoped services) may be resolved per request.
        services.TryAddScoped<IManualToolInvoker, ManualToolInvoker>();

        return services;
    }
}
