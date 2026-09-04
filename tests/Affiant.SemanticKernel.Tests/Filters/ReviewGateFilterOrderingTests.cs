namespace Affiant.SemanticKernel.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Extensions;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// P5a ordering proof (item 1 of this wave / affiant#25; see <see cref="Affiant.Core.Filters.ReviewGateFilter"/>'s
/// class remarks, "Why registration order no longer matters"): unlike a host's own
/// <c>IAutoFunctionInvocationFilter</c>, the framework's <c>ReviewGateFilter</c> is a neutral filter
/// that runs INSIDE <see cref="AffiantAutoFunctionInvocationBridge"/>'s own pipeline — it never
/// competed for position in SK's filter list, so it never needed HR Portal's
/// <c>kernel.AutoFunctionInvocationFilters.Insert(0, ...)</c> workaround. This test proves that
/// empirically, not by assumption: the services are wired using ONLY the standard, appended
/// <c>AddAffiantSemanticKernel()</c> DI chain (no special registration order, no Insert(0), no
/// manual filter construction bypassing DI for the filter itself), and a write proposal requiring
/// human review still ends the turn.
/// </summary>
public class ReviewGateFilterOrderingTests
{
    [Fact]
    public async Task RequiresReview_EndsTurn_ThroughNormalAppendedRegistration_NoInsertZeroNeeded()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The only host-owned pieces (no default framework implementation for any of these).
        // Deliberately NO IApprovalPolicy — the evaluator's fallback (ReviewerConfirmation) forces
        // RequiresReview, the exact branch that must terminate the turn.
        services.AddSingleton<IStreamingTransport>(new NoOpTransport());
        services.AddSingleton<IDocketStore>(new InMemoryDocketStore());
        services.AddSingleton<IReviewContextProvider>(new ConstantReviewContextProvider(BuildReviewContext()));
        // AddAffiantCore() also registers UiGuidanceBridge (area-4 P1f(b)), which needs
        // IRouteRegistry resolvable for ValidateOnBuild below, even though this test never
        // exercises guidance.
        services.AddSingleton<IRouteRegistry>(new NoOpRouteRegistry());

        // Standard chain, standard order — exactly what a host's Program.cs does. No
        // kernel.AutoFunctionInvocationFilters.Insert(0, ...), no bespoke filter class.
        services.AddAffiantCore(o => o.EnableObservability = false);
        services.AddAffiantSemanticKernel();

        var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        // The bridge itself is what AddAffiantSkFilters() registers as the sole
        // IAutoFunctionInvocationFilter SK ever sees — resolving it here (rather than hand-
        // constructing it with hand-picked dependencies) proves the DI-registered instance, wired
        // exactly as a host's kernel would receive it, produces the terminating behavior.
        using var scope = sp.CreateScope();
        var bridge = Assert.IsType<AffiantAutoFunctionInvocationBridge>(
            Assert.Single(scope.ServiceProvider.GetServices<IAutoFunctionInvocationFilter>()));

        var writeProposalJson =
            """{"$type":"write","toolName":"DoWrite","timestamp":"2026-01-01T00:00:00Z","envelope":null}""";
        var kernel = new Kernel(scope.ServiceProvider);
        var function = KernelFunctionFactory.CreateFromMethod(() => "unused", "DoWrite");
        var initialResult = new FunctionResult(function, writeProposalJson);
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, "calling DoWrite");
        var context = new AutoFunctionInvocationContext(kernel, function, initialResult, new ChatHistory(), chatMessage);

        // Simulates SK's own remaining chain: no other filters, tool already ran (context.Result
        // already carries its output) — exactly what SK hands the last-registered filter.
        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        Assert.True(context.Terminate, "RequiresReview must end the turn through normal DI registration alone.");
        var resultText = context.Result.GetValue<object>() as string;
        Assert.NotNull(resultText);
        Assert.Contains("filed for review", resultText, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ReviewContext BuildReviewContext() => new(
        SessionId: "session-ordering-proof",
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

    private sealed class NoOpTransport : IStreamingTransport
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

    private sealed class InMemoryDocketStore : IDocketStore
    {
        private readonly Dictionary<Guid, DocketEntry> _entries = [];

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<ConversationContext?>(null);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
        {
            _entries[entry.EntryId] = entry;
            return Task.CompletedTask;
        }

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct) =>
            Task.FromResult(_entries.TryGetValue(entryId, out var e) ? e : null);

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
}
