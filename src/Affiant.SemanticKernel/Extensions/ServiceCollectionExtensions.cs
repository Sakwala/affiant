namespace Affiant.SemanticKernel.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Services;
using Affiant.Core.Triggers;
using Affiant.SemanticKernel.Adapters;
using Affiant.SemanticKernel.Connectors;
using Affiant.SemanticKernel.Filters;
using Affiant.SemanticKernel.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;

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
    ///   SK auto-function invocation filter pipeline — post-tool positions 6 and 7 per L2 PRD §"Task 4".
    ///   (Positions 1–2 are <c>ToolErrorFilter</c> and <c>DeterministicShortCircuit</c>
    ///   registered by <c>AddAffiantCore()</c>. Position 3 is host-provided
    ///   <c>ContextExtractor</c> subclasses. Positions 4–5 are the pre-tool L2 filters
    ///   registered by <c>AddAffiantInferenceOrchestration()</c>. Position 6 is
    ///   <c>TaskInferenceMergeFilter</c>; position 7 is <c>ReviewGateFilter</c> — both
    ///   registered here.)
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
    ///
    /// <remarks>
    /// For L2 inference orchestration (Epic 16 / Story 16.3+), hosts should call
    /// <see cref="AddAffiantInferenceOrchestration"/> separately, typically
    /// immediately after <see cref="AddAffiantSemanticKernel"/>. The two extensions
    /// are independent — <see cref="AddAffiantSemanticKernel"/> wires the startup
    /// validator + the post-tool merge + review-gate filters; <see
    /// cref="AddAffiantInferenceOrchestration"/> wires the pre-tool inference filters
    /// and the SK inference port. Both are required for full L2 behavior.
    /// </remarks>
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

        // Filter pipeline: post-tool positions 6 and 7 per L2 PRD §"Task 4"
        // (TaskInferenceMergeFilter + ReviewGateFilter). See AffiantFilterPipeline for full order.
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

    /// <summary>
    /// Registers the complete Affiant L2 inference-orchestration stack in one call.
    ///
    /// Registers (all with TryAdd semantics — host can override before calling this):
    /// <list type="bullet">
    ///   <item><c>IInferenceCompletionPort</c> → <c>SemanticKernelInferenceCompletionPort</c> (Scoped)</item>
    ///   <item><c>TaskInferenceRunner</c> (Scoped)</item>
    ///   <item><c>IInferenceTrigger</c> enumerable entry → <c>WriteIntentInferenceTrigger</c> (Singleton)</item>
    ///   <item><c>IAffidavitProjection</c> enumerable entry → <c>SchemaDrivenAffidavitProjection</c> (Scoped)</item>
    ///   <item><c>IFunctionInvocationFilter</c> enumerable entry → <c>ToolArgumentCaptureFilter</c> (Scoped)</item>
    ///   <item><c>IFunctionInvocationFilter</c> enumerable entry → <c>InferenceTriggerFilter</c> (Scoped)</item>
    /// </list>
    ///
    /// Host contracts (must be populated before inference fires):
    /// <list type="bullet">
    ///   <item><c>kernel.Data["ChatHistory"]</c> — <c>ChatHistory</c> for the current conversation turn.
    ///         InferenceTriggerFilter reads this to build the inference prompt; falls back to empty ChatHistory.</item>
    ///   <item><c>kernel.Data["ConversationId"]</c> — string conversation identifier for idempotency bookkeeping.
    ///         Falls back to the ContextFabric instance hash if absent.</item>
    ///   <item><c>kernel.Data["AffiantTurnNumber"]</c> — int turn counter for idempotency bookkeeping.
    ///         Falls back to 0 if absent (more conservative: deduplicates across turns for the same function).</item>
    /// </list>
    ///
    /// Prerequisites: call <c>AddAffiantCore()</c> before this method. Calling without prior
    /// <c>AddAffiantCore()</c> produces a runtime DI resolution failure (no <c>IAffiantToolRegistry</c>,
    /// no <c>IContextFabric</c>, no <c>TaskInferenceStep</c>). Does NOT call <c>AddAffiantSemanticKernel()</c>
    /// or <c>AddAffiantCore()</c> internally — those are independent extensions hosts wire separately.
    ///
    /// Pipeline ordering is NOT established here — that is Story 16.4's territory.
    /// The two new pre-tool filters land in DI's IFunctionInvocationFilter enumerable;
    /// 16.4's AffiantFilterPipeline edit locks the execution order.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <returns>The <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddAffiantInferenceOrchestration(
        this IServiceCollection services)
    {
        // Port: SK-side IInferenceCompletionPort — wraps IChatCompletionService with
        // FunctionChoiceBehavior.None() and structured-output prompt construction.
        services.TryAddScoped<IInferenceCompletionPort, SemanticKernelInferenceCompletionPort>();

        // Runner: stateless orchestrator from Affiant.Core (16.2).
        services.TryAddScoped<TaskInferenceRunner>();

        // Default trigger: WriteIntentInferenceTrigger fires inference when the tool is a
        // registered write-intent operation (WriteCreate/WriteUpdate). Singleton — stateless.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IInferenceTrigger, WriteIntentInferenceTrigger>());

        // Default projection slot: SchemaDrivenAffidavitProjection driven by ITaskInferenceStrategy.
        // Requires ITaskInferenceStrategy to be registered by the host via AddAffiantTool<TStrategy>.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAffidavitProjection, SchemaDrivenAffidavitProjection>());

        // Pre-tool filter pair (pipeline ordering is 16.4's job — registered into the
        // IFunctionInvocationFilter enumerable that SK pulls from when building FunctionInvocationFilters).
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IFunctionInvocationFilter, ToolArgumentCaptureFilter>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IFunctionInvocationFilter, InferenceTriggerFilter>());

        return services;
    }
}
