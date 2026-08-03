namespace Affiant.Core.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// <c>ContextFabric</c> / <c>IContextFabric</c> (conversation entity + provenance state) and
    /// <c>TaskInferenceStep</c> (which captures the fabric) are registered <b>Scoped</b> — one instance
    /// per conversation turn scope. Hosts MUST NOT re-register the fabric as a singleton: doing so bleeds
    /// values across concurrent conversations and races a global <c>Clear()</c> against live projections.
    /// <c>ApprovalPolicyEvaluator</c> / <c>IApprovalPolicyEvaluator</c> (policy pipeline) is registered
    /// <b>Scoped</b> (affiant#19) — it constructor-injects <c>IEnumerable&lt;IApprovalPolicy&gt;</c>, and
    /// Affiant.Policies registers policies Scoped by default, so a singleton evaluator would be a
    /// captive dependency the moment a policy has a scoped dependency (e.g. a host <c>DbContext</c>).
    /// The following services are registered as Singletons by this method, in this registration
    /// (= onion entry / outermost-first) order (area-3 P2 ruling 2 — <c>ToolErrorFilter</c> is
    /// registered before <c>DeterministicShortCircuit</c> so it is genuinely outermost, matching
    /// framework spec §3.12.4):
    /// <list type="bullet">
    /// <item><c>ToolErrorFilter</c> as <c>IToolInvocationFilter</c> — error handling with retry</item>
    /// <item><c>DeterministicShortCircuit</c> as <c>IToolInvocationFilter</c> — pre-LLM interception</item>
    /// <item><c>ToolTracingFilter</c> as <c>IToolInvocationFilter</c> — per-tool <c>execute_tool</c> OTel span</item>
    /// <item><c>ToolInvocationPipeline</c> — backend-neutral runner owning canonical filter order</item>
    /// </list>
    /// No <c>IApprovalPolicy</c> is registered by default. Hosts must call
    /// <c>AddAffiantPolicies()</c> from Affiant.Policies to declare their policy graph.
    /// <c>ReviewGate</c> is registered here too (affiant#26) — see the dedicated remarks below.
    /// Services whose lifetimes depend on adapter-specific types outside <c>Affiant.Abstractions</c>
    /// (<c>SessionRehydrator</c> needs SK's <c>ChatHistory</c>; <c>UiGuidanceBridge</c> is a thin
    /// pass-through a host wires directly onto its own transport call sites) are intentionally
    /// omitted and must be registered directly by the host with the appropriate lifetime.
    /// <para>
    /// <b>ReviewGate (affiant#26):</b> registered <b>Scoped</b> here, matching
    /// <c>ApprovalPolicyEvaluator</c>'s lifetime (affiant#19) — it constructor-injects
    /// <c>IApprovalPolicyEvaluator</c> (itself Scoped), so a Singleton <c>ReviewGate</c> would be a
    /// captive dependency the instant a policy carries a Scoped dependency (e.g. a host
    /// <c>DbContext</c>), exactly the failure mode affiant#19 already fixed for the evaluator itself.
    /// <c>ReviewGate</c>'s other two constructor dependencies — <c>IStreamingTransport</c> and
    /// <c>IDocketStore</c> — are both <c>Affiant.Abstractions</c> interfaces with no default
    /// implementation in this package; <c>TryAddScoped</c> here does not require them to already be
    /// registered (DI resolves lazily at first use, not at registration time), it only requires the
    /// host to have registered <em>some</em> implementation (typically via
    /// <c>Affiant.Transport.SignalR</c>'s <c>AddAffiantSignalR</c> and <c>Affiant.Docket</c>'s
    /// <c>AddAffiantDocket</c>) before actually resolving <c>ReviewGate</c>. Before this fix, every
    /// host had to hand-register <c>ReviewGate</c> itself — <c>ReviewGateFilter</c> resolves it via
    /// <c>context.Services.GetService&lt;ReviewGate&gt;()</c> (not <c>GetRequiredService</c>) and
    /// silently no-ops when it is absent, so a host that forgot the manual registration got no error,
    /// only silently-unfiled write proposals.
    /// </para>
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

        // Default in-process observability channel — always registered; hosts override before AddAffiantCore().
        services.TryAddSingleton<IObservabilityEventStream<AffidavitEmittedEvent>, InMemoryObservabilityEventStream<AffidavitEmittedEvent>>();

        // Step 1: Entity state tracking. SCOPED — the fabric is a conversation-scoped store (framework
        // spec §7 / tool-authoring-guide). One instance per turn scope isolates concurrent
        // conversations; a singleton fabric shares un-namespaced keys across conversations (value bleed)
        // and a global Clear() would race a concurrent conversation's provenance to Empty. Hosts MUST
        // NOT re-register it as a singleton.
        services.TryAddScoped<ContextFabric>();
        // IContextFabric alias — resolved as the same scoped instance so adapters can depend on the abstraction.
        services.TryAddScoped<IContextFabric>(sp => sp.GetRequiredService<ContextFabric>());

        // Step 3: Structured-output merge logic. SCOPED because it captures the scoped ContextFabric —
        // a singleton here would be a captive dependency pinning one conversation's fabric.
        services.TryAddScoped<TaskInferenceStep>();

        // Step 5: Policy evaluation pipeline. SCOPED (affiant#19) — it constructor-injects
        // IEnumerable<IApprovalPolicy>, and Affiant.Policies' builder registers policies Scoped by
        // default (a policy commonly needs a per-request dependency such as a host DbContext). A
        // singleton evaluator would capture that scoped policy list once at root-scope construction:
        // a captive dependency that throws under ValidateOnBuild/ValidateScopes, or — where
        // validation is off — silently pins one request's scoped instances (e.g. a DbContext) for
        // the process lifetime, shared unsafely across concurrent evaluations.
        services.TryAddScoped<ApprovalPolicyEvaluator>();
        services.TryAddScoped<IApprovalPolicyEvaluator>(
            sp => sp.GetRequiredService<ApprovalPolicyEvaluator>());

        // No default IApprovalPolicy registered here — hosts declare their policy graph
        // via AddAffiantPolicies() in Affiant.Policies. The evaluator's built-in fallback
        // returns ReviewerConfirmation when no policy matches.

        // Step 6: Review state machine (affiant#26). SCOPED — see this method's remarks for why
        // (mirrors ApprovalPolicyEvaluator's affiant#19 captive-dependency fix). Resolving it still
        // requires the host to have registered IStreamingTransport and IDocketStore somewhere in the
        // same composition root; this TryAddScoped only removes the need to also hand-register
        // ReviewGate itself.
        services.TryAddScoped<ReviewGate>();

        // Backend-neutral pipeline runner — owns canonical filter order + per-invocation DI scope.
        services.TryAddSingleton<ToolInvocationPipeline>();

        // Step 1 / area-3 P2 ruling 2: Error-handling filter — registered FIRST so it is the
        // genuine outermost neutral filter (framework spec §3.12.4 and ToolErrorFilter's own class
        // doc both require this; before this fix, DeterministicShortCircuit was registered first
        // and was the actual outermost filter — a documented-but-unenforced order the position
        // paper's evidence pack flagged (area-3 V4) and AffiantFilterPipelineOrderTests now locks
        // explicitly). Outermost means a bug in a host's IIntentInterceptor (which
        // DeterministicShortCircuit invokes, next) also becomes a typed ToolError instead of
        // propagating raw out of the neutral pipeline.
        services.TryAddSingleton<ToolErrorFilter>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IToolInvocationFilter, ToolErrorFilter>());

        // Step 2: Pre-LLM intent interception — now wrapped by ToolErrorFilter (see above).
        services.TryAddSingleton<DeterministicShortCircuit>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IToolInvocationFilter, DeterministicShortCircuit>());

        // Step 10: Per-tool OTel span — creates execute_tool span for all hosts automatically
        services.TryAddSingleton<ToolTracingFilter>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IToolInvocationFilter, ToolTracingFilter>());

        // Step 13: Telemetry infrastructure (idempotent static initialisation)
        if (options.EnableObservability)
        {
            _ = AffiantTelemetry.AffiantActivitySource;
            _ = AffiantTelemetry.AffiantMeter;
        }

        services.AddSingleton(options);

        return services;
    }

    /// <summary>
    /// Registers the two completion-stage neutral filters — <see cref="ReviewGateFilter"/> and
    /// <see cref="TaskInferenceMergeFilter"/> — in the one order both interception backends must use.
    ///
    /// Both filters do all their work <em>after</em> <c>await next()</c> (post-tool), so on the onion
    /// unwind the filter registered <em>last</em> runs its post-work <em>first</em>. The framework spec
    /// §3.12.4 requires <see cref="TaskInferenceMergeFilter"/>'s merge to <em>complete</em> before
    /// <see cref="ReviewGateFilter"/> files the review (the reviewer must see a fully-merged Affidavit),
    /// so the merge filter must be innermost: <see cref="ReviewGateFilter"/> is registered first
    /// (outermost), <see cref="TaskInferenceMergeFilter"/> last (innermost, post-work runs first).
    ///
    /// Single source of truth so the SK bridge (<c>AddAffiantSkFilters</c>) and the MAF adapter
    /// (<c>AddAffiantAgentFramework</c>) cannot drift on this ordering. Both filters are Scoped:
    /// resolved per invocation from the pipeline runner's DI scope.
    /// </summary>
    public static IServiceCollection AddAffiantCompletionFilters(this IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IToolInvocationFilter, ReviewGateFilter>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IToolInvocationFilter, TaskInferenceMergeFilter>());
        return services;
    }

    /// <summary>
    /// Registers a write-intent tool's strategy in DI and a matching descriptor in the registry — atomically.
    /// Call <c>services.AddAffiantCore()</c> first.
    /// </summary>
    /// <remarks>
    /// Uses <c>TryAddSingleton</c> for the strategy so a host's prior registration wins.
    /// If <paramref name="operation"/> is <see cref="Operation.ReadQuery"/>, throws <see cref="ArgumentException"/>;
    /// use <see cref="AddAffiantReadTool"/> for read tools instead.
    /// </remarks>
    public static IServiceCollection AddAffiantTool<TStrategy>(
        this IServiceCollection services,
        string functionName,
        Operation operation,
        string entityType,
        string? pluginName = null)
        where TStrategy : class, ITaskInferenceStrategy
    {
        if (operation == Operation.ReadQuery)
            throw new ArgumentException(
                $"AddAffiantTool<{typeof(TStrategy).Name}>(): Operation.ReadQuery is not a valid write operation. " +
                "Use AddAffiantReadTool() for read tools.",
                nameof(operation));

        var registry = ResolveRegistry(services);
        services.TryAddSingleton<TStrategy>();
        registry.Register(new AffiantToolDescriptor(
            functionName, pluginName, operation, entityType, typeof(TStrategy)));
        return services;
    }

    /// <summary>
    /// Registers a read-only tool descriptor in the registry. No strategy DI registration occurs.
    /// Call <c>services.AddAffiantCore()</c> first.
    /// </summary>
    public static IServiceCollection AddAffiantReadTool(
        this IServiceCollection services,
        string functionName,
        string? entityType = null,
        string? pluginName = null)
    {
        var registry = ResolveRegistry(services);
        registry.Register(new AffiantToolDescriptor(
            functionName, pluginName, Operation.ReadQuery, entityType, InferenceStrategy: null));
        return services;
    }

    /// <summary>
    /// Registers a schema-driven affidavit projection in DI. Multiple distinct projections
    /// (one per entity type) may be registered; all resolve via <c>GetServices&lt;IAffidavitProjection&gt;()</c>.
    /// Calling with the same <typeparamref name="TProjection"/> twice is a no-op.
    /// </summary>
    public static IServiceCollection AddAffidavitProjection<TProjection>(
        this IServiceCollection services)
        where TProjection : class, IAffidavitProjection
    {
        // Idempotency guard: skip if TProjection was already registered.
        if (services.Any(d => d.ServiceType == typeof(TProjection)))
            return services;

        services.AddSingleton<TProjection>();
        // Factory keeps IAffidavitProjection and TProjection sharing the same singleton instance.
        services.AddSingleton<IAffidavitProjection>(sp => sp.GetRequiredService<TProjection>());
        return services;
    }

    /// <summary>
    /// Registers a deterministic field source in DI. Multiple sources may be registered for
    /// the same or different field names; all resolve via <c>GetServices&lt;IDeterministicFieldSource&gt;()</c>.
    /// Calling with the same <typeparamref name="TSource"/> twice is a no-op.
    /// </summary>
    /// <remarks>
    /// <see cref="IDeterministicFieldSource"/> is obsolete — see <see cref="AddFieldResolver{TResolver}"/>
    /// for its async, DI-scope-friendly successor. Kept fully functional for existing hosts.
    /// </remarks>
#pragma warning disable CS0618 // IDeterministicFieldSource is obsolete but kept fully functional — see type XML docs.
    public static IServiceCollection AddDeterministicFieldSource<TSource>(
        this IServiceCollection services)
        where TSource : class, IDeterministicFieldSource
    {
        // Idempotency guard: skip if TSource was already registered.
        if (services.Any(d => d.ServiceType == typeof(TSource)))
            return services;

        services.AddSingleton<TSource>();
        services.AddSingleton<IDeterministicFieldSource>(sp => sp.GetRequiredService<TSource>());
        return services;
    }
#pragma warning restore CS0618

    /// <summary>
    /// Registers an <see cref="IFieldResolver"/> in DI. Multiple resolvers may be registered for
    /// the same or different field names; all resolve via <c>GetServices&lt;IFieldResolver&gt;()</c>.
    /// Calling with the same <typeparamref name="TResolver"/> twice is a no-op.
    /// </summary>
    /// <remarks>
    /// Registered <b>Scoped</b> — deliberately unlike <see cref="AddDeterministicFieldSource{TSource}"/>'s
    /// Singleton lifetime — so a resolver may take a DI-scoped dependency (e.g. a per-request
    /// lookup client) without becoming a captive dependency. This lines up with the default
    /// single-strategy <c>IAffidavitProjection</c> registration (also Scoped, via
    /// <c>AddAffiantInferenceOrchestration</c>'s conditional default in Affiant.SemanticKernel) and
    /// with the multi-strategy <see cref="AddSchemaDrivenProjection{TStrategy}"/> path, which also
    /// registers its projection Scoped. The concern below is not really about resolver
    /// dependencies — a Scoped resolver has no captive-dependency risk in isolation. It is about the
    /// <em>projection that consumes</em> <c>IEnumerable&lt;IFieldResolver&gt;</c>: if that projection
    /// is ever registered as a Singleton (as <see cref="AddSchemaDrivenProjection{TStrategy}"/> once
    /// did), resolving it captures the Scoped resolvers at singleton construction time — a captive
    /// dependency that throws under <c>ServiceProviderOptions.ValidateScopes</c> and, without
    /// validation, silently pins one request's resolver instances for the app's lifetime. Both
    /// projection registration paths in this framework are Scoped specifically to avoid this; any
    /// future projection registration must be too, or must guarantee its resolvers have no Scoped
    /// dependencies.
    /// </remarks>
    public static IServiceCollection AddFieldResolver<TResolver>(
        this IServiceCollection services)
        where TResolver : class, IFieldResolver
    {
        // Idempotency guard: skip if TResolver was already registered.
        if (services.Any(d => d.ServiceType == typeof(TResolver)))
            return services;

        services.AddScoped<TResolver>();
        services.AddScoped<IFieldResolver>(sp => sp.GetRequiredService<TResolver>());
        return services;
    }

    /// <summary>
    /// Factory-registers an <see cref="IAffidavitProjection"/> bound to a specific <see cref="ITaskInferenceStrategy"/>
    /// concrete type. Designed for multi-strategy hosts (e.g., a portal with several write-tool domains).
    /// Each call registers an independent projection instance targeting one strategy's EntityName.
    /// Call before <see cref="Affiant.SemanticKernel.Extensions.ServiceCollectionExtensions.AddAffiantInferenceOrchestration"/>
    /// so the conditional-default check in that method finds the host-registered projections and skips the default.
    /// </summary>
    /// <typeparam name="TStrategy">The concrete <see cref="ITaskInferenceStrategy"/> to bind the projection to.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Registered <b>Scoped</b> (not Singleton) — mirroring the default single-strategy projection
    /// registration in <c>AddAffiantInferenceOrchestration</c> (Affiant.SemanticKernel). The
    /// constructed <see cref="SchemaDrivenAffidavitProjection"/> takes <c>IEnumerable&lt;IFieldResolver&gt;</c>,
    /// and <see cref="AddFieldResolver{TResolver}"/> registers resolvers Scoped by design (so they may
    /// carry Scoped dependencies such as a per-request lookup client). A Singleton projection here
    /// would resolve that enumerable once at first construction — a captive dependency that throws
    /// under <c>ServiceProviderOptions.ValidateScopes</c> for any host combining this method with
    /// <see cref="AddFieldResolver{TResolver}"/> (the documented multi-strategy pattern), and would
    /// otherwise silently pin one scope's resolver instances for the process lifetime.
    /// </remarks>
    public static IServiceCollection AddSchemaDrivenProjection<TStrategy>(this IServiceCollection services)
        where TStrategy : class, ITaskInferenceStrategy
    {
        // Uses AddScoped (not TryAddScoped) so each strategy gets its own projection instance in the
        // enumerable — multiple calls with different TStrategy types add independently, which is the
        // intent. Scoped (not Singleton — see remarks above) so the resolved IEnumerable<IFieldResolver>
        // and IEnumerable<IDeterministicFieldSource> are never captured outside their own scope.
        // ActivatorUtilities resolves the remaining constructor parameters (resolvers, deterministic
        // sources, logger, event stream) from the service provider, binding the passed TStrategy
        // instance to the strategy slot.
        services.AddScoped<IAffidavitProjection>(sp =>
            ActivatorUtilities.CreateInstance<SchemaDrivenAffidavitProjection>(
                sp, sp.GetRequiredService<TStrategy>()));
        return services;
    }

    private static IAffiantToolRegistry ResolveRegistry(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IAffiantToolRegistry))
            ?? throw new InvalidOperationException(
                "IAffiantToolRegistry is not registered. " +
                "Call services.AddAffiantCore() before services.AddAffiantTool<>() or services.AddAffiantReadTool().");

        if (descriptor.ImplementationInstance is IAffiantToolRegistry existingInstance)
            return existingInstance;

        if (descriptor.ImplementationType is not null)
        {
            var fresh = Activator.CreateInstance(descriptor.ImplementationType) as IAffiantToolRegistry
                ?? throw new InvalidOperationException(
                    $"Activator.CreateInstance({descriptor.ImplementationType.Name}) did not produce an IAffiantToolRegistry.");

            // Pin the instance so the built ServiceProvider returns the same singleton already filled here.
            services.Remove(descriptor);
            services.AddSingleton<IAffiantToolRegistry>(fresh);
            return fresh;
        }

        throw new InvalidOperationException(
            "IAffiantToolRegistry is registered with a factory delegate, which is not supported by " +
            "AddAffiantTool or AddAffiantReadTool. Use a type registration (TryAddSingleton<IAffiantToolRegistry, TImpl>()) " +
            "or an instance registration (AddSingleton<IAffiantToolRegistry>(instance)).");
    }
}
