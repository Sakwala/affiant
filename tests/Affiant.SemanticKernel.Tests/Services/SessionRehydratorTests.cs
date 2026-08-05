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

        public Task<int> TryConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
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
                .ToList();
            return Task.FromResult(pending);
        }

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
            => throw new InvalidOperationException("not used by this test");

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
            => throw new InvalidOperationException("not used by this test");
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
