namespace Affiant.SemanticKernel.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Adapter reality check (area-3-tool-calling-reliability.md item 4; affiant#22 / FV-9): drives
/// <see cref="AffiantAutoFunctionInvocationBridge"/> — the SK <em>completion-stage</em> bridge where
/// <see cref="ReviewGateFilter"/> actually runs — directly against a manually constructed
/// <see cref="AutoFunctionInvocationContext"/>. SK exposes a public constructor for exactly this
/// case (<c>AutoFunctionInvocationContext(Kernel, KernelFunction, FunctionResult, ChatHistory,
/// ChatMessageContent)</c>), so no full chat-completion round trip is needed to exercise the real
/// bridge code — this is not a re-implementation of it.
///
/// <para>
/// <b>Why this matters (the load-bearing question):</b> V4 found that on SK, the completion stage
/// (<c>ReviewGateFilter</c>/<c>TaskInferenceMergeFilter</c>) runs in a <em>separate</em>
/// <c>pipeline.RunAsync</c> call with <c>ToolErrorFilter</c> structurally absent, so an exception
/// that escaped <c>ReviewGateFilter</c> uncaught would propagate raw into SK's own auto-invocation
/// loop — no Affiant-owned safety net exists at that seam on SK the way it does on MAF's single
/// onion. P1a's fix works around this entirely: <c>ReviewGateFilter</c> now catches its own filing
/// failure and rewrites <see cref="ToolInvocationContext.Result"/> itself, so nothing needs to
/// escape the pipeline.RunAsync call at all. This test proves that rewritten result really does
/// flow back onto <see cref="AutoFunctionInvocationContext.Result"/> (via
/// <c>AffiantAutoFunctionInvocationBridge</c>'s <c>ReferenceEquals</c> check), which is what makes it
/// reach the model on SK exactly as it does on MAF (see the sibling MAF test,
/// <c>AffiantFunctionInvocationMiddlewareTests.ReviewGateFilter_FilingThrows_MiddlewareReturnsTypedToolError_NotTheRawProposal</c>).
/// </para>
/// </summary>
public class AffiantAutoFunctionInvocationBridgeReviewGateTests
{
    [Fact]
    public async Task ReviewGateFilter_FilingThrows_AutoInvocationContextResultBecomesTypedToolError()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ReviewGate>();
        services.AddSingleton(new AffiantCoreOptions());
        services.AddSingleton<IDocketStore>(new ThrowingDocketStore());
        services.AddSingleton<IStreamingTransport>(new RecordingStreamingTransport());
        services.AddSingleton<IApprovalPolicy>(new StandingOrderPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();
        services.AddSingleton<IReviewContextProvider>(new ConstantReviewContextProvider(BuildReviewContext()));
        services.AddSingleton<IToolInvocationFilter>(
            new ReviewGateFilter(NullLogger<ReviewGateFilter>.Instance));

        var sp = services.BuildServiceProvider();
        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());
        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);

        using var scope = sp.CreateScope();
        var writeProposalJson =
            """{"$type":"write","toolName":"DoWrite","timestamp":"2026-01-01T00:00:00Z","envelope":null}""";
        var context = BuildAutoInvocationContext(scope.ServiceProvider, "DoWrite", writeProposalJson);

        // Simulates SK's own remaining auto-invocation chain — a no-op here since context.Result
        // already carries the tool's (already-executed) output, exactly as SK hands it to the real
        // IAutoFunctionInvocationFilter position.
        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        var resultText = context.Result.GetValue<object>() as string;
        Assert.NotNull(resultText);
        Assert.Contains("REVIEW_FILING_FAILED", resultText);
        Assert.Contains("\"$type\":\"error\"", resultText);
        Assert.DoesNotContain("\"$type\":\"write\"", resultText); // not the raw, unfiled proposal
    }

    [Fact]
    public async Task ReviewGateFilter_FilingSucceeds_AutoInvocationContextResultUnchanged()
    {
        // Control case: StandingOrder auto-approves without touching the result — proves the previous
        // test's ToolError rewrite is caused by the filing failure, not by driving the bridge directly.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ReviewGate>();
        services.AddSingleton(new AffiantCoreOptions());
        services.AddSingleton<IDocketStore>(new InMemoryDocketStore());
        services.AddSingleton<IStreamingTransport>(new RecordingStreamingTransport());
        services.AddSingleton<IApprovalPolicy>(new StandingOrderPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();
        services.AddSingleton<IReviewContextProvider>(new ConstantReviewContextProvider(BuildReviewContext()));
        services.AddSingleton<IToolInvocationFilter>(
            new ReviewGateFilter(NullLogger<ReviewGateFilter>.Instance));

        var sp = services.BuildServiceProvider();
        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());
        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);

        using var scope = sp.CreateScope();
        var writeProposalJson =
            """{"$type":"write","toolName":"DoWrite","timestamp":"2026-01-01T00:00:00Z","envelope":null}""";
        var context = BuildAutoInvocationContext(scope.ServiceProvider, "DoWrite", writeProposalJson);

        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        var resultText = context.Result.GetValue<object>() as string;
        Assert.Equal(writeProposalJson, resultText);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AutoFunctionInvocationContext BuildAutoInvocationContext(
        IServiceProvider services, string functionName, string initialResultJson)
    {
        var kernel = new Kernel(services);
        var function = KernelFunctionFactory.CreateFromMethod(() => "unused", functionName);
        var initialResult = new FunctionResult(function, initialResultJson);
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, $"calling {functionName}");
        return new AutoFunctionInvocationContext(kernel, function, initialResult, new ChatHistory(), chatMessage);
    }

    private static ReviewContext BuildReviewContext() => new(
        SessionId: "session-sk-test",
        TenantId: "tenant-default",
        UserId: "user-123",
        ReviewerUserId: "reviewer-456",
        Affidavit: new Affidavit(
            OperationType: "DoWrite",
            EntityType: "TestEntity",
            EntityId: null,
            Fields: [],
            AggregateConfidence: 1.0f,
            Warnings: [],
            RequiresConfirmation: true));

    private sealed class ConstantReviewContextProvider(ReviewContext context) : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => context;
    }

    private sealed class StandingOrderPolicy : IApprovalPolicy
    {
        public Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
            => Task.FromResult<ReviewRequirement?>(ReviewRequirement.StandingOrder);
    }

    private sealed class ThrowingDocketStore : IDocketStore
    {
        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => Task.CompletedTask;

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<ConversationContext?>(null);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
            => throw new InvalidOperationException("simulated docket store outage");

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
            => Task.FromResult(0);

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

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

    private sealed class InMemoryDocketStore : IDocketStore
    {
        private readonly Dictionary<Guid, DocketEntry> _entries = [];

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => Task.CompletedTask;

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<ConversationContext?>(null);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
        {
            _entries[entry.EntryId] = entry;
            return Task.CompletedTask;
        }

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult(_entries.TryGetValue(entryId, out var e) ? e : null);

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
        {
            if (!_entries.TryGetValue(entryId, out var existing) || existing.Status != ReviewStatus.Pending)
                return Task.FromResult(0);
            _entries[entryId] = existing with { Status = status };
            return Task.FromResult(1);
        }

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
        {
            if (!_entries.TryGetValue(entryId, out var existing)
                || existing.Status != ReviewStatus.Expired
                || existing.ResubmittedTo is not null)
            {
                return Task.FromResult(0);
            }
            _entries[entryId] = existing with { ResubmittedTo = newEntryId };
            return Task.FromResult(1);
        }

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult(_entries.Values.FirstOrDefault(e => e.ResubmittedTo == entryId));

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

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

    private sealed class RecordingStreamingTransport : IStreamingTransport
    {
        public List<(string GroupId, TransportEvent EventType, object Payload)> Broadcasts { get; } = [];

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new InvalidOperationException("should not be called");

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            Broadcasts.Add((groupId, eventType, payload));
            return Task.CompletedTask;
        }

        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException("should not be called");
    }
}
