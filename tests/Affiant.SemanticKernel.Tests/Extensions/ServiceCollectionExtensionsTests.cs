namespace Affiant.SemanticKernel.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Connectors;
using Affiant.SemanticKernel.Extensions;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Verifies that AddAffiantSemanticKernel() registers all required framework services.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAffiantSemanticKernel_RegistersSemanticKernelOptions_WithDefaults()
    {
        var sp = BuildMinimalProvider();
        var options = sp.GetRequiredService<SemanticKernelOptions>();
        Assert.Equal("AzureOpenAI", options.PrimaryProvider);
        Assert.Equal("Gemini", options.FallbackProvider);
        Assert.True(options.EnableAutoFunctionInvocation);
        Assert.True(options.EnableManualInvocationFallback);
        Assert.Equal(3, options.MaxAutoInvocationRetries);
        Assert.False(options.EnableFilterLogging);
    }

    [Fact]
    public void AddAffiantSemanticKernel_AppliesConfigureCallback()
    {
        var sp = BuildMinimalProvider(configure: opts =>
        {
            opts.PrimaryProvider = "google";
            opts.FallbackProvider = "openai";
            opts.EnableFilterLogging = true;
            opts.MaxAutoInvocationRetries = 5;
        });

        var options = sp.GetRequiredService<SemanticKernelOptions>();
        Assert.Equal("google", options.PrimaryProvider);
        Assert.Equal("openai", options.FallbackProvider);
        Assert.True(options.EnableFilterLogging);
        Assert.Equal(5, options.MaxAutoInvocationRetries);
    }

    [Fact]
    public void AddAffiantSemanticKernel_WorksWithNullConfigure()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantSemanticKernel();
        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<SemanticKernelOptions>());
    }

    [Fact]
    public void AddAffiantSemanticKernel_RegistersCapabilityRegistry()
    {
        var sp = BuildMinimalProvider();
        var registry = sp.GetRequiredService<CapabilityRegistry>();
        Assert.NotNull(registry);
        // Verify the registry resolves known providers
        Assert.True(registry.Resolve("openai").SupportsAutoFunctionInvocationFilter);
        Assert.True(registry.Resolve("google").SupportsAutoFunctionInvocationFilter);
    }

    [Fact]
    public void AddAffiantSemanticKernel_RegistersManualToolInvoker_AsScoped()
    {
        // ManualToolInvoker depends on ToolInvocationPipeline (it runs the completion segment), which
        // AddAffiantCore registers — the SK adapter documents that AddAffiantCore is called first.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddAffiantSemanticKernel();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IManualToolInvoker>();
        Assert.NotNull(invoker);
        Assert.IsType<ManualToolInvoker>(invoker);
    }

    [Fact]
    public void AddAffiantSemanticKernel_RegistersTaskInferenceMergeFilter_AsToolInvocationFilter()
    {
        var sp = BuildProviderWithInferenceStack();
        using var scope = sp.CreateScope();
        var filters = scope.ServiceProvider.GetServices<IToolInvocationFilter>().ToList();
        Assert.NotEmpty(filters);
        Assert.Single(filters, f => f is TaskInferenceMergeFilter);
    }

    [Fact]
    public void AddAffiantSemanticKernel_RegistersReviewGateFilter_AsToolInvocationFilter()
    {
        var sp = BuildProviderWithInferenceStack();
        using var scope = sp.CreateScope();
        var filters = scope.ServiceProvider.GetServices<IToolInvocationFilter>().ToList();
        Assert.NotEmpty(filters);
        Assert.Single(filters, f => f is ReviewGateFilter);
    }

    [Fact]
    public void AddAffiantSemanticKernel_FilterPipeline_ReviewGateEnteredBeforeMerge_SoMergeCompletesFirst()
    {
        // Completion-stage ordering contract (framework spec §3.12.4): both TaskInferenceMergeFilter
        // and ReviewGateFilter do their work after await next() (post-tool), so on the onion unwind
        // the filter entered LAST runs its post-work FIRST. To make the merge COMPLETE before the
        // review is filed, ReviewGateFilter must be entered (registered) outer/first and
        // TaskInferenceMergeFilter inner/last. Registration order is therefore review-before-merge.
        var sp = BuildProviderWithInferenceStack();
        using var scope = sp.CreateScope();
        var filters = scope.ServiceProvider.GetServices<IToolInvocationFilter>().ToList();

        var taskInferenceIdx = filters.FindIndex(f => f is TaskInferenceMergeFilter);
        var reviewGateIdx = filters.FindIndex(f => f is ReviewGateFilter);

        Assert.True(taskInferenceIdx >= 0, "TaskInferenceMergeFilter must be registered");
        Assert.True(reviewGateIdx >= 0, "ReviewGateFilter must be registered");
        Assert.True(reviewGateIdx < taskInferenceIdx,
            "ReviewGateFilter must be entered before TaskInferenceMergeFilter so the merge's post-work " +
            "(inner, unwinds first) completes before the review is filed");
    }

    [Fact]
    public void AddAffiantSemanticKernel_HostRegisteredCapabilityRegistry_TakesPreference()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var hostRegistry = new CapabilityRegistry();
        services.AddSingleton(hostRegistry);         // host registers first
        services.AddAffiantSemanticKernel();         // TryAdd must skip it
        var sp = services.BuildServiceProvider();

        var resolved = sp.GetRequiredService<CapabilityRegistry>();
        Assert.Same(hostRegistry, resolved);
    }

    [Fact]
    public void AddAffiantSemanticKernel_ChainsWith_AddAffiantCore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskInferenceStrategy, StubTaskInferenceStrategy>();
        services.AddScoped<ContextFabric>();
        services.AddScoped<TaskInferenceStep>();
        services.AddAffiantSemanticKernel(opts => opts.PrimaryProvider = "openai");
        services.AddAffiantCore(opts => opts.EnableObservability = false);

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<SemanticKernelOptions>());
        Assert.NotNull(sp.GetRequiredService<CapabilityRegistry>());
        Assert.NotNull(sp.GetRequiredService<AffiantCoreOptions>());
    }

    [Fact]
    public void AddAffiantSemanticKernel_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        var returned = services.AddAffiantSemanticKernel();
        Assert.Same(services, returned);
    }

    // affiant#26 boot-honesty test: a host that calls ONLY the public Add* extensions — no manual
    // ReviewGate/ReviewGateFilter/ApprovalPolicyEvaluator registration — must get a fully wired
    // filing path. Registers just the two genuinely host-owned pieces every host has to supply
    // regardless (IStreamingTransport, IDocketStore, IReviewContextProvider — none of these have a
    // default framework implementation, by domain-agnostic design) plus AddAffiantCore() +
    // AddAffiantSemanticKernel(), then drives the real AffiantAutoFunctionInvocationBridge (the
    // actual seam ReviewGateFilter runs at) with a WriteProposal tool result.
    //
    // Proof that ReviewGate was actually resolved and invoked (not silently skipped, which is
    // ReviewGateFilter's behavior when context.Services.GetService<ReviewGate>() returns null): the
    // docket store is rigged to throw on filing, which ReviewGateFilter converts into a typed
    // REVIEW_FILING_FAILED ToolError. A null ReviewGate would leave the original WriteProposal JSON
    // untouched instead — this is the same shape check
    // AffiantAutoFunctionInvocationBridgeReviewGateTests.cs uses, but built from only the public
    // extension surface rather than a hand-assembled ServiceCollection.
    [Fact]
    public async Task AddAffiantCore_AddAffiantSemanticKernel_Alone_WireReviewGate_FilingFilterRuns()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The only host-owned pieces (no default framework implementation exists for any of these —
        // by the domain-agnostic invariant, IReviewContextProvider in particular must always be
        // host-supplied). Everything else below this line is the public Add* extension chain alone.
        services.AddSingleton<IStreamingTransport>(new NoOpStreamingTransport());
        services.AddSingleton<IDocketStore>(new ThrowingDocketStore());
        services.AddSingleton<IReviewContextProvider>(new ConstantReviewContextProvider(BuildReviewContext()));
        // AddAffiantCore() also registers UiGuidanceBridge (area-4 P1f(b)), which needs
        // IRouteRegistry resolvable for ValidateOnBuild below, even though this test never
        // exercises guidance.
        services.AddSingleton<IRouteRegistry>(new NoOpRouteRegistry());

        services.AddAffiantCore(o => o.EnableObservability = false);
        services.AddAffiantSemanticKernel();

        var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        var pipeline = sp.GetRequiredService<ToolInvocationPipeline>();
        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);

        using var scope = sp.CreateScope();
        var writeProposalJson =
            """{"$type":"write","toolName":"DoWrite","timestamp":"2026-01-01T00:00:00Z","envelope":null}""";
        var kernel = new Kernel(scope.ServiceProvider);
        var function = KernelFunctionFactory.CreateFromMethod(() => "unused", "DoWrite");
        var initialResult = new FunctionResult(function, writeProposalJson);
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, "calling DoWrite");
        var context = new AutoFunctionInvocationContext(kernel, function, initialResult, new ChatHistory(), chatMessage);

        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        var resultText = context.Result.GetValue<object>() as string;
        Assert.NotNull(resultText);
        Assert.Contains("REVIEW_FILING_FAILED", resultText);
        Assert.DoesNotContain("\"$type\":\"write\"", resultText); // proves ReviewGate ran, not skipped
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ReviewContext BuildReviewContext() => new(
        SessionId: "session-boot-honesty",
        TenantId: "tenant-default",
        UserId: "user-123",
        ReviewerUserId: "reviewer-456",
        Affidavit: new Affidavit(
            OperationType: "DoWrite",
            EntityType: "TestEntity",
            EntityId: null,
            // A substantive field: the gate refuses a proposal that swears to nothing (GT-3),
            // so a fixture exercising the filing path has to swear to something.
            Fields: [new AffidavitField("field", "value", null,
                ProvenanceChain.From(ProvenanceTag.FromTool("fixture")))],
            AggregateConfidence: 0.9f,
            PopulatedConfidence: 0.9f,
            EmptyFieldCount: 0,
            Warnings: [],
            RequiresConfirmation: true));

    private sealed class ConstantReviewContextProvider(ReviewContext context) : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => context;
    }

    private sealed class NoOpStreamingTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct) =>
            Task.CompletedTask;
        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default) =>
            Task.FromCanceled<EvidenceCardResponse>(ct);
    }

    private sealed class NoOpRouteRegistry : IRouteRegistry
    {
        public void Register(GuidableElement element) { }
        public IReadOnlyList<GuidableElement> GetElementsForRoute(string route) => [];
        public IReadOnlyList<GuidableElement> GetAllElements() => [];
        public GuidableElement? GetElementById(string elementId) => null;
    }

    private sealed class ThrowingDocketStore : IDocketStore
    {
        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<ConversationContext?>(null);
        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct) =>
            throw new InvalidOperationException("simulated docket store outage");
        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct) =>
            Task.FromResult<DocketEntry?>(null);
        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct) =>
            Task.FromResult<DocketEntry?>(null);
        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>([]);
        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>([]);

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

    private static ServiceProvider BuildMinimalProvider(
        Action<SemanticKernelOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantSemanticKernel(configure);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildProviderWithInferenceStack()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskInferenceStrategy, StubTaskInferenceStrategy>();
        services.AddSingleton<IAffiantToolRegistry>(new AffiantToolRegistry());
        services.AddScoped<ContextFabric>();
        services.AddScoped<TaskInferenceStep>();
        services.AddAffiantSemanticKernel();
        return services.BuildServiceProvider();
    }

    private sealed class StubTaskInferenceStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "StubEntity";
        public IReadOnlyList<TaskInferenceField> Fields => Array.Empty<TaskInferenceField>();
        public double? MinimumConfidenceThreshold => null;
    }
}
