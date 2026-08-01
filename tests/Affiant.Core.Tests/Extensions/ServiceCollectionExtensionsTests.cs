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
            o.PrimaryProvider = "Gemini";
            o.DefaultDocketTtl = TimeSpan.FromMinutes(20);
        }).BuildServiceProvider();

        var opts = sp.GetRequiredService<AffiantCoreOptions>();
        Assert.Equal("Gemini", opts.PrimaryProvider);
        Assert.Equal(TimeSpan.FromMinutes(20), opts.DefaultDocketTtl);
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

    // --- Stubs for adapter interfaces ---

    private sealed class StubStreamingTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct) => Task.CompletedTask;
        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct) => Task.CompletedTask;
        public async IAsyncEnumerable<TransportMessage> ReceiveAsync(string connectionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public Task<T> AwaitEventAsync<T>(string sessionGroupId, Guid docketId, CancellationToken ct) => Task.FromCanceled<T>(ct);
    }

    private sealed class StubDocketStore : IDocketStore
    {
        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct) => Task.CompletedTask;
        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct) => Task.FromResult<ConversationContext?>(null);
        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct) => Task.CompletedTask;
        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct) => Task.FromResult<DocketEntry?>(null);
        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct) => Task.FromResult(0);
        public Task UpdateAmendmentsAsync(Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>(Array.Empty<DocketEntry>());
        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>(Array.Empty<DocketEntry>());
        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubChatSessionStore : IChatSessionStore
    {
        public Task<ChatSession> CreateAsync(string tenantId, string userId, CancellationToken ct) =>
            Task.FromResult(new ChatSession(Guid.NewGuid().ToString(), tenantId, userId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        public Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct) => Task.FromResult<ChatSession?>(null);
        public Task SaveMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct) => Task.CompletedTask;
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
