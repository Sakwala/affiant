namespace Affiant.Core.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Verifies that AddAffiantCore() correctly registers all framework services.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    private static IServiceCollection BuildWithStubs(Action<AffiantCoreOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Stubs for adapter-package interfaces the host must provide
        services.AddSingleton<IStreamingTransport, StubStreamingTransport>();
        services.AddSingleton<IDocketStore, StubDocketStore>();
        services.AddSingleton<IChatSessionStore, StubChatSessionStore>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IRouteRegistry, StubRouteRegistry>();
        services.AddSingleton<ITaskInferenceStrategy, StubTaskInferenceStrategy>();

        services.AddAffiantCore(configure);
        return services;
    }

    [Fact]
    public void AddAffiantCore_registers_all_services()
    {
        var sp = BuildWithStubs(o => o.EnableObservability = false).BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<ContextFabric>());
        Assert.NotNull(sp.GetRequiredService<TaskInferenceStep>());
        Assert.NotNull(sp.GetRequiredService<ApprovalPolicyEvaluator>());
        Assert.NotNull(sp.GetRequiredService<IApprovalPolicyEvaluator>());
        Assert.NotNull(sp.GetRequiredService<DeterministicShortCircuit>());
        Assert.NotNull(sp.GetRequiredService<ToolErrorFilter>());
        Assert.NotNull(sp.GetRequiredService<ToolTracingFilter>());
        Assert.NotNull(sp.GetRequiredService<AffiantCoreOptions>());
    }

    // affiant#26: ReviewGate now gets a real registration from AddAffiantCore() — a host no longer
    // hand-registers it just so ReviewGateFilter's context.Services.GetService<ReviewGate>() finds
    // something. Scoped (not the outer test's singleton-checking assertions) because it depends on
    // the Scoped IApprovalPolicyEvaluator — resolved here inside a scope, matching real usage.
    [Fact]
    public void AddAffiantCore_registers_ReviewGate_AsScoped_ResolvableFromHostStubsAlone()
    {
        var sp = BuildWithStubs(o => o.EnableObservability = false).BuildServiceProvider();

        using var scope = sp.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<ReviewGate>();
        Assert.NotNull(gate);

        // Distinct instances across scopes — Scoped, not accidentally Singleton (which would be a
        // captive dependency on the Scoped ApprovalPolicyEvaluator inside it, affiant#19's class of
        // bug recurring for ReviewGate specifically).
        using var otherScope = sp.CreateScope();
        var gateInOtherScope = otherScope.ServiceProvider.GetRequiredService<ReviewGate>();
        Assert.NotSame(gate, gateInOtherScope);
    }

    [Fact]
    public void AddAffiantCore_does_not_register_default_approval_policy()
    {
        // Hosts declare their policy graph via AddAffiantPolicies() in Affiant.Policies.
        // The evaluator's built-in fallback returns ReviewerConfirmation when no policy matches.
        var sp = BuildWithStubs().BuildServiceProvider();

        var policies = sp.GetServices<IApprovalPolicy>().ToList();
        Assert.Empty(policies);
    }

    [Fact]
    public void AddAffiantCore_stores_options_and_they_are_retrievable()
    {
        var sp = BuildWithStubs(o =>
        {
            o.DefaultDocketTtl = TimeSpan.FromMinutes(20);
            o.SystemPrompt = "You are a test host.";
        }).BuildServiceProvider();

        var opts = sp.GetRequiredService<AffiantCoreOptions>();
        Assert.Equal(TimeSpan.FromMinutes(20), opts.DefaultDocketTtl);
        Assert.Equal("You are a test host.", opts.SystemPrompt);
    }

    // --- Captive-dependency lock test: AddSchemaDrivenProjection<TStrategy>() (multi-strategy path)
    // + AddFieldResolver<T>() (Scoped resolvers) must resolve under ValidateScopes: true. Before the
    // fix, AddSchemaDrivenProjection<TStrategy>() registered IAffidavitProjection as a Singleton that
    // resolved IEnumerable<IFieldResolver> at construction — a captive dependency on the Scoped
    // resolver registered by AddFieldResolver<T>(), which throws InvalidOperationException under
    // ValidateScopes for exactly this documented multi-strategy-projection + resolver combination.
    [Fact]
    public void AddSchemaDrivenProjection_WithFieldResolver_ResolvesAndProjectsUnderValidateScopes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IObservabilityEventStream<AffidavitEmittedEvent>, InMemoryObservabilityEventStream<AffidavitEmittedEvent>>();

        // Mirrors what AddAffiantTool<TStrategy>() does for the strategy's own DI slot.
        services.AddSingleton<StubTaskInferenceStrategy>();

        services.AddSchemaDrivenProjection<StubTaskInferenceStrategy>();
        services.AddFieldResolver<StubFieldResolver>();

        // ValidateScopes: true is the setting that turns a captive dependency into a hard failure
        // at resolve time — the same setting ASP.NET Core's default host enables in Development.
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        var projection = Assert.Single(scope.ServiceProvider.GetServices<IAffidavitProjection>());

        // Actually call the projection inside the scope — resolving is necessary but not sufficient;
        // the regression this guards against is a resolve-time throw under ValidateScopes.
        var fabric = new ContextFabric();
        var affidavit = projection.Project(fabric, "WriteCreate", Array.Empty<string>());

        Assert.Equal("StubEntity", affidavit.EntityType);
    }

    [Fact]
    public void AddAffiantCore_preserves_host_registered_policies()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStreamingTransport, StubStreamingTransport>();
        services.AddSingleton<IDocketStore, StubDocketStore>();
        services.AddSingleton<IChatSessionStore, StubChatSessionStore>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IRouteRegistry, StubRouteRegistry>();
        services.AddSingleton<ITaskInferenceStrategy, StubTaskInferenceStrategy>();

        // Simulate host pre-registering a custom policy before calling AddAffiantCore
        services.AddSingleton<IApprovalPolicy, StubApprovalPolicy>();
        services.AddAffiantCore(o => o.EnableObservability = false);

        var sp = services.BuildServiceProvider();
        var policies = sp.GetServices<IApprovalPolicy>().ToList();

        // Only the custom policy — Core no longer injects a default.
        Assert.Single(policies);
        Assert.Contains(policies, p => p is StubApprovalPolicy);
    }

    // --- Captive-dependency lock test (affiant#19): ApprovalPolicyEvaluator constructor-injects
    // IEnumerable<IApprovalPolicy>, and Affiant.Policies registers policies Scoped by default (a
    // policy commonly needs a per-request dependency such as a host DbContext). Before the fix,
    // AddAffiantCore() registered ApprovalPolicyEvaluator/IApprovalPolicyEvaluator as Singleton — a
    // captive dependency the moment a policy had a Scoped dependency: a hard failure under
    // ValidateOnBuild/ValidateScopes (the settings ASP.NET Core's Development host enables by
    // default), and — where validation is off — a silently shared, process-lifetime instance of what
    // should be a per-request dependency (e.g. an EF DbContext, which is not thread-safe) across
    // every concurrent evaluation.
    [Fact]
    public async Task ApprovalPolicyEvaluator_WithScopedPolicyDependency_ResolvesUnderRealHostValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStreamingTransport, StubStreamingTransport>();
        services.AddSingleton<IDocketStore, StubDocketStore>();
        services.AddSingleton<IChatSessionStore, StubChatSessionStore>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IRouteRegistry, StubRouteRegistry>();
        services.AddSingleton<ITaskInferenceStrategy, StubTaskInferenceStrategy>();

        // Mimics Affiant.Policies' AddStandingOrder<TPolicy>()/AddReferralRule<TRule>() default —
        // both register IApprovalPolicy as Scoped unless the host overrides the lifetime — and
        // mimics a host policy (e.g. LeaveApprovalPolicy) that itself depends on a Scoped service
        // (e.g. a host DbContext).
        services.AddScoped<StubScopedPolicyDependency>();
        services.AddScoped<IApprovalPolicy, ScopedPolicyWithScopedDependency>();

        services.AddAffiantCore(o => o.EnableObservability = false);

        // (a) Construction must succeed under the exact validation a Development host applies:
        // ValidateScopes catches captive dependencies at resolve time, ValidateOnBuild catches them
        // eagerly at BuildServiceProvider() — this line itself is the assertion for (a).
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        // (b) Resolving IApprovalPolicyEvaluator inside a scope works and evaluates the policy.
        using (var scope = provider.CreateScope())
        {
            var evaluator = scope.ServiceProvider.GetRequiredService<IApprovalPolicyEvaluator>();
            var affidavit = new Affidavit(
                OperationType: "WriteCreate",
                EntityType: "StubEntity",
                EntityId: null,
                Fields: [],
                AggregateConfidence: 1.0f,
                Warnings: [],
                RequiresConfirmation: true);
            var requirement = await evaluator.EvaluateAsync(affidavit);
            Assert.Equal(ReviewRequirement.StandingOrder, requirement);
        }

        // (c) Two separate scopes get DISTINCT policy-dependency instances — kills the
        // shared-instance concurrency hazard (an undisposed, process-wide DbContext shared across
        // concurrent evaluations), not just the ValidateScopes/ValidateOnBuild error.
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        _ = scopeA.ServiceProvider.GetRequiredService<IApprovalPolicyEvaluator>();
        _ = scopeB.ServiceProvider.GetRequiredService<IApprovalPolicyEvaluator>();

        var dependencyA = Assert.Single(scopeA.ServiceProvider.GetServices<IApprovalPolicy>()
            .OfType<ScopedPolicyWithScopedDependency>()).Dependency;
        var dependencyB = Assert.Single(scopeB.ServiceProvider.GetServices<IApprovalPolicy>()
            .OfType<ScopedPolicyWithScopedDependency>()).Dependency;

        Assert.NotSame(dependencyA, dependencyB);
    }

    private sealed class StubScopedPolicyDependency;

    private sealed class ScopedPolicyWithScopedDependency(StubScopedPolicyDependency dependency) : IApprovalPolicy
    {
        public StubScopedPolicyDependency Dependency => dependency;

        public Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReviewRequirement?>(ReviewRequirement.StandingOrder);
    }

    // --- Stubs for adapter interfaces ---

    private sealed class StubStreamingTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct) => Task.CompletedTask;
        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct) => Task.CompletedTask;
        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default) => Task.FromCanceled<EvidenceCardResponse>(ct);
    }

    private sealed class StubDocketStore : IDocketStore
    {
        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct) => Task.CompletedTask;
        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct) => Task.FromResult<ConversationContext?>(null);
        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct) => Task.CompletedTask;
        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct) => Task.FromResult<DocketEntry?>(null);
        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct) => Task.FromResult(0);
        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct) => Task.FromResult(0);
        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct) => Task.FromResult<DocketEntry?>(null);
        public Task UpdateAmendmentsAsync(Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>(Array.Empty<DocketEntry>());
        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>(Array.Empty<DocketEntry>());

        // ── The scoped, guarded, paged surface ──────────────────────────────
        // Explicit implementations that refuse: this double exists for a test that never reaches
        // the Docket's decision surface, and a stub that quietly answered would let such a test
        // pass against behaviour nobody wrote.
        Task<DocketTransitionResult> IDocketStore.TransitionAsync(
            Guid entryId, DocketScope scope, ReviewStatus expected, DocketTransitionPatch patch, CancellationToken ct)
            => throw new NotSupportedException();

        Task<PreserveAmendmentsResult> IDocketStore.PreserveAmendmentsAsync(
            Guid entryId, DocketScope scope, IReadOnlyDictionary<string, object?> amendments,
            PreservedAct act, CancellationToken ct)
            => throw new NotSupportedException();

        Task<RecordExecutionResult> IDocketStore.RecordExecutionAsync(
            Guid entryId, DocketScope scope, ExecutionOutcome outcome, string? detail,
            ExecutionOutcome expected, CancellationToken ct)
            => throw new NotSupportedException();

        Task<RecordSupersessionResult> IDocketStore.RecordSupersessionAsync(
            Guid entryId, DocketScope scope, Guid supersededBy, CancellationToken ct)
            => throw new NotSupportedException();

        Task<int> IDocketStore.MarkBlockedAsync(Guid entryId, BlockedMarker marker, CancellationToken ct)
            => Task.FromResult(0);

        Task<DocketPageResult<DocketEntry>> IDocketStore.ListPendingAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
            => Task.FromResult(new DocketPageResult<DocketEntry>([], null, false));

        Task<DocketPageResult<DocketEntry>> IDocketStore.ListApprovedUnexecutedAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
            => Task.FromResult(new DocketPageResult<DocketEntry>([], null, false));

        Task<ExpireDueResult> IDocketStore.ExpireDueAsync(
            DateTimeOffset now, DocketScope scope, int limit, CancellationToken ct)
            => Task.FromResult(new ExpireDueResult([], false));

        Task<RetentionResult> IDocketStore.ApplyRetentionAsync(
            DocketRetentionPolicy policy, DocketScope scope, int limit, CancellationToken ct)
            => throw new NotSupportedException();

        Task<int> IDocketStore.PurgeTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotSupportedException();

        IAsyncEnumerable<DocketEntry> IDocketStore.ExportAsync(DocketScope scope, CancellationToken ct)
            => throw new NotSupportedException();
}

    private sealed class StubChatSessionStore : IChatSessionStore
    {
        public Task<ChatSession> CreateAsync(string tenantId, string userId, CancellationToken ct) =>
            Task.FromResult(new ChatSession(Guid.NewGuid().ToString(), tenantId, userId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        public Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct) => Task.FromResult<ChatSession?>(null);
        public Task SaveMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct) => Task.CompletedTask;
        public Task AppendMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AffiantChatMessage>> LoadMessagesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AffiantChatMessage>>(Array.Empty<AffiantChatMessage>());
        public Task DeleteAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubRouteRegistry : IRouteRegistry
    {
        public void Register(GuidableElement element) { }
        public IReadOnlyList<GuidableElement> GetElementsForRoute(string route) => Array.Empty<GuidableElement>();
        public IReadOnlyList<GuidableElement> GetAllElements() => Array.Empty<GuidableElement>();
        public GuidableElement? GetElementById(string elementId) => null;
    }

    private sealed class StubTaskInferenceStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "StubEntity";
        public IReadOnlyList<TaskInferenceField> Fields => Array.Empty<TaskInferenceField>();
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class StubFieldResolver : IFieldResolver
    {
        public string FieldName => "SomeField";
        public Task<FieldResolution?> ResolveAsync(FieldResolutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult<FieldResolution?>(null);
    }

    private sealed class StubApprovalPolicy : IApprovalPolicy
    {
        public Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReviewRequirement?>(null);
    }
}
