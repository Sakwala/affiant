using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Docket.Tests.Fixtures;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Affiant.Docket.Tests;

/// <summary>
/// Backend-parity suite for the Affiant protocol's DK-1 clause "expiry is a queryable state": an entry past
/// its deadline reads as Expired whether or not any sweep has run, on an inclusive boundary. Runs
/// against all three <see cref="IDocketStore"/> implementations over an injected fake clock, so the
/// only thing that moves between assertions is time.
/// </summary>
/// <remarks>
/// The projection is a read-time one. The persisted row stays Pending until the sweep — or a
/// decision, or a resubmission — commits the transition, and each case below proves that too:
/// <c>UpdateReviewStatusAsync</c>'s <c>WHERE Status = 'Pending'</c> guard still returns 1 for an
/// entry the reads already call Expired.
/// </remarks>
public sealed class ExpiryAsQueryableStateTests
{
    private static readonly DateTimeOffset Origin = FakeClockDocketStoreProviderFactory.Origin;

    [Theory]
    [ClassData(typeof(FakeClockDocketStoreProviderFactory))]
    public async Task GetDocketEntryAsync_AtExactlyExpiresAt_ReadsExpired_BeforeAnySweep(
        IDocketStore store, FakeTimeProvider clock, string providerName)
    {
        Assert.NotEmpty(providerName);
        var deadline = Origin.AddMinutes(10);
        var entry = TestDocketEntry.CreateDefault(expiresAt: deadline);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var beforeDeadline = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Pending, beforeDeadline!.Status);

        // The boundary is inclusive: AT ExpiresAt the entry is expired.
        clock.SetUtcNow(deadline);

        var atDeadline = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Expired, atDeadline!.Status);

        // Nothing has been written: the guarded transition is still there for the sweep to win.
        var rows = await store.UpdateReviewStatusAsync(
            entry.EntryId, ReviewStatus.Expired, CancellationToken.None);
        Assert.Equal(1, rows);
    }

    [Theory]
    [ClassData(typeof(FakeClockDocketStoreProviderFactory))]
    public async Task GetDocketEntryAsync_OneTickBeforeExpiresAt_StillReadsPending(
        IDocketStore store, FakeTimeProvider clock, string providerName)
    {
        Assert.NotEmpty(providerName);
        var deadline = Origin.AddMinutes(10);
        var entry = TestDocketEntry.CreateDefault(expiresAt: deadline);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        clock.SetUtcNow(deadline.AddMilliseconds(-1));

        var justBefore = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Pending, justBefore!.Status);
    }

    [Theory]
    [ClassData(typeof(FakeClockDocketStoreProviderFactory))]
    public async Task GetDocketEntryAsync_DecidedEntryPastItsDeadline_KeepsItsTerminalStatus(
        IDocketStore store, FakeTimeProvider clock, string providerName)
    {
        Assert.NotEmpty(providerName);
        var deadline = Origin.AddMinutes(10);
        var entry = TestDocketEntry.CreateDefault(expiresAt: deadline);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);
        await store.UpdateReviewStatusAsync(entry.EntryId, ReviewStatus.Approved, CancellationToken.None);

        clock.SetUtcNow(deadline.AddHours(1));

        // The projection applies to Pending rows only — an entry decided inside its window stays
        // decided forever, however long ago the deadline was.
        var read = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Approved, read!.Status);
    }

    [Theory]
    [ClassData(typeof(FakeClockDocketStoreProviderFactory))]
    public async Task PendingListings_PastExpiresAt_NoLongerCarryTheEntry(
        IDocketStore store, FakeTimeProvider clock, string providerName)
    {
        Assert.NotEmpty(providerName);
        var sessionId = Guid.NewGuid().ToString();
        var deadline = Origin.AddMinutes(10);
        var entry = TestDocketEntry.CreateDefault(sessionId: sessionId, expiresAt: deadline);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        Assert.Contains(
            await store.ListPendingBySessionAsync(sessionId, CancellationToken.None),
            e => e.EntryId == entry.EntryId);
        Assert.Contains(
            await store.ListAllPendingAsync(CancellationToken.None),
            e => e.EntryId == entry.EntryId);

        clock.SetUtcNow(deadline);

        // Not pending any more, swept or not — which is also what keeps a rehydrating session from
        // replaying a card that has already run out of time.
        Assert.DoesNotContain(
            await store.ListPendingBySessionAsync(sessionId, CancellationToken.None),
            e => e.EntryId == entry.EntryId);
        Assert.DoesNotContain(
            await store.ListAllPendingAsync(CancellationToken.None),
            e => e.EntryId == entry.EntryId);
    }

    [Theory]
    [ClassData(typeof(FakeClockDocketStoreProviderFactory))]
    public async Task ListExpiredAsync_HonoursItsLimit_AndReturnsTheOldestDeadlinesFirst(
        IDocketStore store, FakeTimeProvider clock, string providerName)
    {
        Assert.NotEmpty(providerName);
        var sessionId = Guid.NewGuid().ToString();
        var deadlines = new[]
        {
            Origin.AddMinutes(1),
            Origin.AddMinutes(2),
            Origin.AddMinutes(3),
            Origin.AddMinutes(4),
            Origin.AddMinutes(5),
        };
        foreach (var deadline in deadlines)
        {
            await store.FileDocketEntryAsync(
                TestDocketEntry.CreateDefault(sessionId: sessionId, expiresAt: deadline),
                CancellationToken.None);
        }

        clock.SetUtcNow(Origin.AddMinutes(10));

        // Unpaged first, so the assertions below hold whatever else the shared Postgres container
        // has due at this instant: our five come back in deadline order...
        var all = await store.ListExpiredAsync(clock.GetUtcNow(), limit: 1000, CancellationToken.None);
        var ours = all.Where(e => e.SessionId == sessionId).ToList();
        Assert.Equal(5, ours.Count);
        Assert.Equal(deadlines, ours.Select(e => e.ExpiresAt).ToArray());

        // ...and a page of two is exactly the first two of that order, never an arbitrary pair.
        var page = await store.ListExpiredAsync(clock.GetUtcNow(), limit: 2, CancellationToken.None);
        Assert.Equal(2, page.Count);
        Assert.Equal(all.Take(2).Select(e => e.EntryId), page.Select(e => e.EntryId));
    }

    [Theory]
    [ClassData(typeof(FakeClockDocketStoreProviderFactory))]
    public async Task ListExpiredAsync_LimitBelowOne_IsRejected(
        IDocketStore store, FakeTimeProvider clock, string providerName)
    {
        Assert.NotEmpty(providerName);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ListExpiredAsync(clock.GetUtcNow(), limit: 0, CancellationToken.None));
    }
}
