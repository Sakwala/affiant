namespace Affiant.SemanticKernel.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.SemanticKernel.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// D2 acceptance criterion 4 (affiant#31): SessionRehydrator re-derives PriorAmendments for a
/// freshly-resubmitted pending entry via IDocketStore.GetResubmissionParentAsync, closing the
/// silent-loss window where EvidenceCardRequest.PriorAmendments only ever travelled on the
/// original, transient resubmission broadcast.
/// </summary>
public sealed class SessionRehydratorTests
{
    private sealed class FakeChatSessionStore : IChatSessionStore
    {
        public Task<ChatSession> CreateAsync(string tenantId, string userId, CancellationToken ct)
            => throw new InvalidOperationException("not used by SessionRehydrator");

        public Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct)
            => throw new InvalidOperationException("not used by SessionRehydrator");

        public Task SaveMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct)
            => throw new InvalidOperationException("not used by SessionRehydrator");

        public Task AppendMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct)
            => throw new InvalidOperationException("not used by SessionRehydrator");

        public Task<IReadOnlyList<AffiantChatMessage>> LoadMessagesAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AffiantChatMessage>>([]);

        public Task DeleteAsync(string sessionId, CancellationToken ct)
            => throw new InvalidOperationException("not used by SessionRehydrator");
    }

    private sealed class FakeDocketStore : IDocketStore
    {
        public List<DocketEntry> Entries { get; } = [];

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => throw new InvalidOperationException("not used by this test");

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<ConversationContext?>(null);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult(Entries.FirstOrDefault(e => e.EntryId == entryId));

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
            => throw new InvalidOperationException("not used by this test");

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => throw new InvalidOperationException("not used by this test");

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult(Entries.FirstOrDefault(e => e.ResubmittedTo == entryId));

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
            => throw new InvalidOperationException("not used by this test");

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
        {
            IReadOnlyList<DocketEntry> pending = Entries
                .Where(e => e.SessionId == sessionId && e.Status == ReviewStatus.Pending)
                .OrderBy(e => e.CreatedAt)
                .ToList();
            return Task.FromResult(pending);
        }

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
        {
            IReadOnlyList<DocketEntry> pending = Entries
                .Where(e => e.Status == ReviewStatus.Pending)
                .ToList();
            return Task.FromResult(pending);
        }

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

        /// <summary>Pending entries in filing order, unpaged — this double holds a handful of rows.</summary>
        Task<DocketPageResult<DocketEntry>> IDocketStore.ListPendingAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
        {
            IReadOnlyList<DocketEntry> items = Entries
                .Where(e => DocketRow.InScope(e, scope) && e.Status == ReviewStatus.Pending)
                .OrderBy(e => e.CreatedAt)
                .ToList();
            return Task.FromResult(new DocketPageResult<DocketEntry>(items, null, false));
        }

        /// <summary>Approved-but-unreported entries in filing order — the second half of the rehydration order.</summary>
        Task<DocketPageResult<DocketEntry>> IDocketStore.ListApprovedUnexecutedAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
        {
            IReadOnlyList<DocketEntry> items = Entries
                .Where(e => DocketRow.InScope(e, scope) && DocketRow.IsApprovedUnexecuted(e))
                .OrderBy(e => e.CreatedAt)
                .ToList();
            return Task.FromResult(new DocketPageResult<DocketEntry>(items, null, false));
        }

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

    private static DocketEntry CreateEntry(
        Guid entryId,
        string sessionId,
        ReviewStatus status,
        IReadOnlyDictionary<string, object?>? amendments = null,
        Guid? resubmittedTo = null)
    {
        var affidavit = new Affidavit(
            OperationType: "CreateOrder",
            EntityType: "Order",
            EntityId: null,
            Fields: [],
            AggregateConfidence: 0.9f,
            Warnings: [],
            RequiresConfirmation: true);

        return new DocketEntry(
            EntryId: entryId,
            SessionId: sessionId,
            TenantId: "tenant-default",
            UserId: "user-123",
            ReviewerUserId: "reviewer-456",
            OperationType: "CreateOrder",
            Envelope: affidavit,
            Status: status,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
            Amendments: amendments,
            ResubmittedTo: resubmittedTo);
    }

    private static SessionRehydrator CreateRehydrator(FakeDocketStore docketStore)
    {
        var config = new ConfigurationBuilder().Build();
        return new SessionRehydrator(
            new FakeChatSessionStore(), docketStore, config, NullLogger<SessionRehydrator>.Instance);
    }

    [Fact]
    public async Task RehydrateAsync_PendingEntryFromResubmission_ReDerivesPriorAmendmentsFromParent()
    {
        const string sessionId = "session-resubmit";
        var priorAmendments = new Dictionary<string, object?> { ["title"] = "Edited before expiry" };
        var expiredParent = CreateEntry(
            Guid.NewGuid(), sessionId, ReviewStatus.Expired, amendments: priorAmendments);
        var resubmittedChild = CreateEntry(Guid.NewGuid(), sessionId, ReviewStatus.Pending);
        var parentWithLineage = expiredParent with { ResubmittedTo = resubmittedChild.EntryId };

        var docketStore = new FakeDocketStore();
        docketStore.Entries.Add(parentWithLineage);
        docketStore.Entries.Add(resubmittedChild);

        var result = await CreateRehydrator(docketStore).RehydrateAsync(sessionId, CancellationToken.None);

        Assert.True(result.PriorAmendmentsByEntryId.ContainsKey(resubmittedChild.EntryId));
        Assert.Equal("Edited before expiry", result.PriorAmendmentsByEntryId[resubmittedChild.EntryId]["title"]);
    }

    [Fact]
    public async Task RehydrateAsync_PendingEntryNotFromResubmission_AbsentFromPriorAmendments()
    {
        const string sessionId = "session-first-time";
        var firstTimeEntry = CreateEntry(Guid.NewGuid(), sessionId, ReviewStatus.Pending);

        var docketStore = new FakeDocketStore();
        docketStore.Entries.Add(firstTimeEntry);

        var result = await CreateRehydrator(docketStore).RehydrateAsync(sessionId, CancellationToken.None);

        Assert.Empty(result.PriorAmendmentsByEntryId);
        Assert.Single(result.PendingEntries);
    }

    [Fact]
    public async Task RehydrateAsync_ResubmittedFromParentWithNoAmendments_AbsentFromPriorAmendments()
    {
        const string sessionId = "session-no-amendments";
        var expiredParent = CreateEntry(Guid.NewGuid(), sessionId, ReviewStatus.Expired, amendments: null);
        var resubmittedChild = CreateEntry(Guid.NewGuid(), sessionId, ReviewStatus.Pending);
        var parentWithLineage = expiredParent with { ResubmittedTo = resubmittedChild.EntryId };

        var docketStore = new FakeDocketStore();
        docketStore.Entries.Add(parentWithLineage);
        docketStore.Entries.Add(resubmittedChild);

        var result = await CreateRehydrator(docketStore).RehydrateAsync(sessionId, CancellationToken.None);

        Assert.Empty(result.PriorAmendmentsByEntryId);
    }
}
