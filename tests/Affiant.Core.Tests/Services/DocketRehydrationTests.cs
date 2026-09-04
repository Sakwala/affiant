namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.Docket.Stores;
using Microsoft.Extensions.Time.Testing;
using Xunit;

/// <summary>
/// The order a reconnecting client is given its Docket back, and the fact that it is paged.
/// </summary>
/// <remarks>
/// Pending entries first, then approved entries whose write has not been reported, each in filing
/// order. The order is a rule rather than a preference because the two groups ask different things
/// of the person reconnecting — one still needs a decision, the other still needs execution — and a
/// client that showed them interleaved would put work that is already agreed in front of work that
/// is still blocked on the reader.
/// </remarks>
public sealed class DocketRehydrationTests
{
    private static readonly DateTimeOffset Origin = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private const string TenantId = "tenant-rehydrate";
    private const string SessionId = "session-rehydrate";

    [Fact]
    public async Task Rehydration_ReturnsPendingFirst_ThenApprovedUnexecuted_EachInFilingOrder()
    {
        var clock = new FakeTimeProvider(Origin);
        var store = new InMemoryDocketStore(clock);

        // Filed interleaved on purpose: filing order within each group must survive, and the group
        // order must not be the filing order.
        var approvedFirst = await FileAsync(store, clock, Origin, approve: true);
        var pendingSecond = await FileAsync(store, clock, Origin.AddMinutes(1), approve: false);
        var approvedThird = await FileAsync(store, clock, Origin.AddMinutes(2), approve: true);
        var pendingFourth = await FileAsync(store, clock, Origin.AddMinutes(3), approve: false);

        clock.SetUtcNow(Origin.AddMinutes(5));
        var sequence = await DocketRehydration.AllAsync(
            store, new DocketScope(TenantId, SessionId), pageSize: 50, CancellationToken.None);

        Assert.Equal(
            [pendingSecond, pendingFourth, approvedFirst, approvedThird],
            sequence.Select(e => e.EntryId));
    }

    [Fact]
    public async Task Rehydration_PagesAcrossTheGroupBoundary_WithoutRestartingTheFirstGroup()
    {
        var clock = new FakeTimeProvider(Origin);
        var store = new InMemoryDocketStore(clock);

        var pendingA = await FileAsync(store, clock, Origin, approve: false);
        var pendingB = await FileAsync(store, clock, Origin.AddMinutes(1), approve: false);
        var approvedC = await FileAsync(store, clock, Origin.AddMinutes(2), approve: true);
        var approvedD = await FileAsync(store, clock, Origin.AddMinutes(3), approve: true);

        clock.SetUtcNow(Origin.AddMinutes(5));
        var scope = new DocketScope(TenantId, SessionId);

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await DocketRehydration.PageAsync(
                store, scope, new DocketPage(2, cursor), CancellationToken.None);
            seen.AddRange(page.Items.Select(e => e.EntryId));
            cursor = page.Cursor;
            pages++;
            Assert.True(pages < 10, "the rehydration sequence did not drain");
        }
        while (cursor is not null);

        // Every row exactly once, in the fixed order, across a boundary that falls between the two
        // groups: the cursor carries which group it is resuming, so the second page starts the
        // approved group rather than repeating the pending one.
        Assert.Equal([pendingA, pendingB, approvedC, approvedD], seen);
    }

    [Fact]
    public async Task Rehydration_NeverReplaysAnEntryPastItsDeadline_SweptOrNot()
    {
        var clock = new FakeTimeProvider(Origin);
        var store = new InMemoryDocketStore(clock);

        var lapsing = await FileAsync(store, clock, Origin, approve: false, ttl: TimeSpan.FromMinutes(1));
        var live = await FileAsync(store, clock, Origin, approve: false, ttl: TimeSpan.FromHours(2));

        // No sweep runs in this test.
        clock.SetUtcNow(Origin.AddMinutes(30));
        var sequence = await DocketRehydration.AllAsync(
            store, new DocketScope(TenantId, SessionId), pageSize: 50, CancellationToken.None);

        Assert.Equal([live], sequence.Select(e => e.EntryId));
        Assert.DoesNotContain(lapsing, sequence.Select(e => e.EntryId));
    }

    [Fact]
    public async Task Rehydration_DropsAnApprovedEntryOnceItsWriteHasBeenReported()
    {
        var clock = new FakeTimeProvider(Origin);
        var store = new InMemoryDocketStore(clock);
        var scope = new DocketScope(TenantId, SessionId);

        var outstanding = await FileAsync(store, clock, Origin, approve: true);
        var reported = await FileAsync(store, clock, Origin.AddMinutes(1), approve: true);
        await store.RecordExecutionAsync(
            reported, new DocketScope(TenantId), ExecutionOutcome.Executed, null,
            ExecutionOutcome.Unexecuted, CancellationToken.None);

        clock.SetUtcNow(Origin.AddMinutes(5));
        var sequence = await DocketRehydration.AllAsync(store, scope, pageSize: 50, CancellationToken.None);

        // What is left in the second group is work outstanding — an approved write nobody has
        // reported on — and nothing else.
        Assert.Equal([outstanding], sequence.Select(e => e.EntryId));
    }

    private static async Task<Guid> FileAsync(
        InMemoryDocketStore store,
        FakeTimeProvider clock,
        DateTimeOffset filedAt,
        bool approve,
        TimeSpan? ttl = null)
    {
        clock.SetUtcNow(filedAt);
        var entryId = Guid.NewGuid();
        var affidavit = Affidavit.Create(
            operationType: "CreateOrder",
            entityType: "Order",
            entityId: null,
            fields: [new AffidavitField(
                "title", "t", null,
                ProvenanceChain.From(ProvenanceTag.FromUser("title", binding: null)))],
            warnings: [],
            requiresConfirmation: false);

        await store.FileDocketEntryAsync(
            new DocketEntry(
                EntryId: entryId,
                SessionId: SessionId,
                TenantId: TenantId,
                UserId: "user-1",
                ReviewerUserId: "reviewer-1",
                OperationType: "CreateOrder",
                Envelope: affidavit,
                Status: ReviewStatus.Pending,
                CreatedAt: filedAt,
                ExpiresAt: filedAt.Add(ttl ?? TimeSpan.FromHours(1)),
                Amendments: null),
            CancellationToken.None);

        if (approve)
        {
            await store.TransitionAsync(
                entryId,
                new DocketScope(TenantId),
                ReviewStatus.Pending,
                new DocketTransitionPatch(ReviewStatus.Approved),
                CancellationToken.None);
        }

        return entryId;
    }
}
