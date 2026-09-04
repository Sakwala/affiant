using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Affiant.Docket.Options;
using Affiant.Docket.Services;
using Affiant.Docket.Stores;
using Affiant.Docket.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Affiant.Docket.Tests.Integration;

/// <summary>
/// Validates DocketExpiryService.ExpireOverdueAsync bulk-update behavior across all three
/// IDocketStore backends. The service uses IServiceScopeFactory to resolve the store,
/// so the test wires up a minimal ServiceProvider with the test store registered as Singleton.
///
/// Key invariants:
///   - Entries past ExpiresAt are transitioned to Expired on the first tick.
///   - Entries not yet past ExpiresAt remain Pending after the first tick.
///   - Running the tick a second time does not corrupt already-Expired entries (idempotent).
///   - When an IStreamingTransport is registered, expiry and warning-window notifications are
///     broadcast to each entry's SessionId group (framework half of repo issue #10 / F0-A6).
/// </summary>
public sealed class DocketExpiryServiceTests
{
    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ExpireOverdueAsync_BulkExpiry_IsIdempotent(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var now = DateTimeOffset.UtcNow;

        // A tenant of this test's own, and a sweep scoped to it. The shared Postgres container backs
        // every store test in this assembly at once, and a sweep left store-wide would expire rows
        // another test class filed a moment earlier — which is also the reason a real deployment
        // that partitions its Docket wants this knob.
        var tenantId = Guid.NewGuid().ToString();
        var expiredEntry1 = TestDocketEntry.CreateDefault(tenantId: tenantId, expiresAt: now.AddSeconds(-5));
        var expiredEntry2 = TestDocketEntry.CreateDefault(tenantId: tenantId, expiresAt: now.AddSeconds(-10));
        var notYetExpired = TestDocketEntry.CreateDefault(tenantId: tenantId, expiresAt: now.AddMinutes(5));

        await store.FileDocketEntryAsync(expiredEntry1, CancellationToken.None);
        await store.FileDocketEntryAsync(expiredEntry2, CancellationToken.None);
        await store.FileDocketEntryAsync(notYetExpired, CancellationToken.None);

        var expiryService = BuildExpiryService(
            store, docketOptions: new AffiantDocketOptions { SweepScope = new DocketScope(tenantId) });

        // First tick: expired entries are transitioned; not-yet-expired stays Pending
        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        var afterFirst1 = await store.GetDocketEntryAsync(expiredEntry1.EntryId, CancellationToken.None);
        var afterFirst2 = await store.GetDocketEntryAsync(expiredEntry2.EntryId, CancellationToken.None);
        var afterFirstPending = await store.GetDocketEntryAsync(notYetExpired.EntryId, CancellationToken.None);

        Assert.Equal(ReviewStatus.Expired, afterFirst1!.Status);
        Assert.Equal(ReviewStatus.Expired, afterFirst2!.Status);
        Assert.Equal(ReviewStatus.Pending, afterFirstPending!.Status);

        // Second tick: already-Expired entries are silently skipped (WHERE Status = Pending guard)
        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        var afterSecond1 = await store.GetDocketEntryAsync(expiredEntry1.EntryId, CancellationToken.None);
        var afterSecond2 = await store.GetDocketEntryAsync(expiredEntry2.EntryId, CancellationToken.None);

        Assert.Equal(ReviewStatus.Expired, afterSecond1!.Status);
        Assert.Equal(ReviewStatus.Expired, afterSecond2!.Status);

        // ExpiresAt is unchanged — the second tick wrote nothing
        Assert.Equal(afterFirst1.ExpiresAt, afterSecond1.ExpiresAt);
        Assert.Equal(afterFirst2.ExpiresAt, afterSecond2.ExpiresAt);
    }

    // ── D5: expiry transport events (issue #10 / F0-A6) ──────────────────────

    [Fact]
    public async Task ExpireOverdueAsync_NoTransportRegistered_DoesNotThrow()
    {
        var store = new InMemoryDocketStore();
        var entry = TestDocketEntry.CreateDefault(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        // transport is left null (default) — the docket package must not hard-require one.
        var expiryService = BuildExpiryService(store, transport: null);

        var ex = await Record.ExceptionAsync(() => expiryService.ExpireOverdueAsync(CancellationToken.None));

        Assert.Null(ex);
        var updated = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Expired, updated!.Status);
    }

    [Fact]
    public async Task ExpireOverdueAsync_PastDeadlineEntry_BroadcastsDocketExpired()
    {
        var store = new InMemoryDocketStore();
        var sessionId = Guid.NewGuid().ToString();
        var entry = TestDocketEntry.CreateDefault(
            sessionId: sessionId, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(store, transport);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        var broadcast = Assert.Single(transport.Broadcasts, b => b.EventType == TransportEvent.DocketExpired);
        Assert.Equal(sessionId, broadcast.GroupId);
        var notification = Assert.IsType<DocketExpiredNotification>(broadcast.Payload);
        Assert.Equal(entry.EntryId, notification.DocketId);
    }

    [Fact]
    public async Task ExpireOverdueAsync_EntryInsideWarningWindow_BroadcastsDocketExpiring_AndStaysPending()
    {
        var store = new InMemoryDocketStore();
        var sessionId = Guid.NewGuid().ToString();
        // Not yet expired, but inside the default 2-minute warning window.
        var entry = TestDocketEntry.CreateDefault(
            sessionId: sessionId, expiresAt: DateTimeOffset.UtcNow.AddSeconds(30));
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(store, transport);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        var broadcast = Assert.Single(transport.Broadcasts, b => b.EventType == TransportEvent.DocketExpiring);
        Assert.Equal(sessionId, broadcast.GroupId);
        var notification = Assert.IsType<DocketExpiringNotification>(broadcast.Payload);
        Assert.Equal(entry.EntryId, notification.DocketId);
        Assert.Equal(entry.ExpiresAt, notification.ExpiresAt);

        Assert.DoesNotContain(transport.Broadcasts, b => b.EventType == TransportEvent.DocketExpired);
        var stillPending = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Pending, stillPending!.Status);
    }

    [Fact]
    public async Task ExpireOverdueAsync_EntryOutsideWarningWindow_NoDocketExpiringBroadcast()
    {
        // Outside the warning window means no DocketExpiring warning — but Area-5 Decision 3's
        // unconditional EvidenceCardRequest re-broadcast (phase 3) still applies regardless of
        // proximity to expiry; see the D3 tests below for that half of this tick's behavior.
        var store = new InMemoryDocketStore();
        var entry = TestDocketEntry.CreateDefault(expiresAt: DateTimeOffset.UtcNow.AddMinutes(10));
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(store, transport);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        Assert.DoesNotContain(transport.Broadcasts, b => b.EventType == TransportEvent.DocketExpiring);
    }

    [Fact]
    public async Task ExpireOverdueAsync_EntryInsideWarningWindow_ReemitsOnEveryTick()
    {
        // Documents the accepted behavior: re-emission across ticks inside the warning window is
        // fine because clients are idempotent (see DocketExpiryService.ExpireOverdueAsync remarks).
        var store = new InMemoryDocketStore();
        var entry = TestDocketEntry.CreateDefault(expiresAt: DateTimeOffset.UtcNow.AddSeconds(30));
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(store, transport);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);
        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        Assert.Equal(2, transport.Broadcasts.Count(b => b.EventType == TransportEvent.DocketExpiring));
    }

    // ── The sweep notifies only for rows ITS OWN write transitioned ────────────────

    [Fact]
    public async Task ExpireOverdueAsync_EntryDecidedBeforeTheSweepReachesIt_NoDocketExpiredForIt_SiblingStillNotified()
    {
        var origin = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(origin);
        var store = new InMemoryDocketStore(clock);
        var sessionId = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid().ToString();

        var racer = TestDocketEntry.CreateDefault(
            sessionId: sessionId, tenantId: tenantId, expiresAt: origin.AddMinutes(5));
        var sibling = TestDocketEntry.CreateDefault(
            sessionId: sessionId, tenantId: tenantId, expiresAt: origin.AddMinutes(1));
        await store.FileDocketEntryAsync(racer, CancellationToken.None);
        await store.FileDocketEntryAsync(sibling, CancellationToken.None);

        // A reviewer decides one of them in time, through the same guarded transition every
        // decision uses.
        var claimed = await store.TransitionAsync(
            racer.EntryId,
            new DocketScope(tenantId),
            ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Approved),
            CancellationToken.None);
        Assert.IsType<DocketTransitionResult.Transitioned>(claimed);

        // Both deadlines pass, and the sweep runs.
        clock.SetUtcNow(origin.AddMinutes(10));
        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(store, transport, timeProvider: clock);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        // The sweep's guard found nothing to write for the racer, so the racer is not in its result
        // and nothing was broadcast for it. Notifying on a row this call did not transition is how a
        // sweep double-notifies.
        Assert.DoesNotContain(
            transport.Broadcasts,
            b => b.EventType == TransportEvent.DocketExpired
                 && ((DocketExpiredNotification)b.Payload).DocketId == racer.EntryId);

        var racerEntry = await store.GetDocketEntryAsync(racer.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Approved, racerEntry!.Status);

        // The sibling was overdue throughout and still gets its notification.
        var siblingBroadcast = Assert.Single(
            transport.Broadcasts,
            b => b.EventType == TransportEvent.DocketExpired
                 && ((DocketExpiredNotification)b.Payload).DocketId == sibling.EntryId);
        Assert.Equal(sessionId, siblingBroadcast.GroupId);

        var siblingEntry = await store.GetDocketEntryAsync(sibling.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Expired, siblingEntry!.Status);
    }

    [Fact]
    public async Task ExpireOverdueAsync_EntryAlreadyExpiredByALateDecision_BroadcastsDocketExpiredExactlyOnce()
    {
        var origin = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(origin);
        var store = new InMemoryDocketStore(clock);
        var sessionId = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid().ToString();

        var racer = TestDocketEntry.CreateDefault(
            sessionId: sessionId, tenantId: tenantId, expiresAt: origin.AddMinutes(1));
        await store.FileDocketEntryAsync(racer, CancellationToken.None);

        clock.SetUtcNow(origin.AddMinutes(10));
        var transport = new SpyStreamingTransport();

        // Exactly what the gate's late-decision path does for a lapsed row: win the guarded
        // transition to Expired, then notify.
        var claimed = await store.TransitionAsync(
            racer.EntryId,
            new DocketScope(tenantId),
            ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Expired),
            CancellationToken.None);
        Assert.IsType<DocketTransitionResult.Transitioned>(claimed);
        await DocketExpiryBroadcaster.VerifyAndBroadcastIfExpiredAsync(
            store, transport, racer.EntryId, CancellationToken.None);

        var expiryService = BuildExpiryService(store, transport, timeProvider: clock);
        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        // One notification, from the caller whose write actually transitioned the row.
        var broadcastsForRacer = transport.Broadcasts.Count(
            b => b.EventType == TransportEvent.DocketExpired
                 && ((DocketExpiredNotification)b.Payload).DocketId == racer.EntryId);
        Assert.Equal(1, broadcastsForRacer);

        var racerEntry = await store.GetDocketEntryAsync(racer.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Expired, racerEntry!.Status);
    }

    // ── D3: at-least-once Evidence Card redelivery (Area-5 Decision 3, affiant#28) ───

    [Fact]
    public async Task ExpireOverdueAsync_PendingEntry_RebroadcastsEvidenceCardRequest_RegardlessOfFilingOutcome()
    {
        var store = new InMemoryDocketStore();
        var sessionId = Guid.NewGuid().ToString();
        // Models affiant#28's stranded-entry window: the entry is durably filed and Pending, but
        // whatever broadcast happened at filing time reached zero group members (or never ran) —
        // irrelevant here, since the sweep's re-broadcast is unconditional on that outcome.
        var entry = TestDocketEntry.CreateDefault(sessionId: sessionId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(store, transport);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        var broadcast = Assert.Single(
            transport.Broadcasts, b => b.EventType == TransportEvent.EvidenceCardRequest);
        Assert.Equal(sessionId, broadcast.GroupId);
        var request = Assert.IsType<EvidenceCardRequest>(broadcast.Payload);
        Assert.Equal(entry.EntryId, request.DocketId);
        Assert.Equal(entry.ExpiresAt, request.RequiredBy);
        Assert.Null(request.PriorAmendments);

        var stillPending = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Pending, stillPending!.Status);
    }

    [Fact]
    public async Task ExpireOverdueAsync_PendingEntry_RebroadcastsAgainOnNextTick()
    {
        // The client that missed the filing-time broadcast, and missed the first sweep tick's
        // re-broadcast, still gets the card on the next tick — at-least-once, repeated until acted
        // on or expired.
        var store = new InMemoryDocketStore();
        var entry = TestDocketEntry.CreateDefault();
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(store, transport);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);
        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        Assert.Equal(2, transport.Broadcasts.Count(b =>
            b.EventType == TransportEvent.EvidenceCardRequest
            && ((EvidenceCardRequest)b.Payload).DocketId == entry.EntryId));
    }

    [Fact]
    public async Task ExpireOverdueAsync_EntryExpiredThisTick_NoEvidenceCardRebroadcast()
    {
        // An entry that phase 1 just transitioned to Expired is no longer Pending by the time
        // phase 3 runs its ListAllPendingAsync query — it must not also get a stray re-broadcast.
        var store = new InMemoryDocketStore();
        var entry = TestDocketEntry.CreateDefault(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(store, transport);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        Assert.DoesNotContain(transport.Broadcasts, b => b.EventType == TransportEvent.EvidenceCardRequest);
    }

    [Fact]
    public async Task ExpireOverdueAsync_NonPendingEntry_NoEvidenceCardRebroadcast()
    {
        var store = new InMemoryDocketStore();
        var entry = TestDocketEntry.CreateDefault(status: ReviewStatus.Approved);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(store, transport);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        Assert.Empty(transport.Broadcasts);
    }

    [Fact]
    public async Task ExpireOverdueAsync_PendingEntryFromResubmission_RebroadcastCarriesPriorAmendments()
    {
        // Same shared builder the filing path uses (EvidenceCardRequestFactory) — the sweep's
        // re-broadcast for a resubmitted entry must carry the parent's amendments too.
        var store = new InMemoryDocketStore();
        var sessionId = Guid.NewGuid().ToString();
        var priorAmendments = new Dictionary<string, object?> { ["title"] = "Edited before expiry" };

        var childId = Guid.NewGuid();
        var parent = TestDocketEntry.CreateDefault(sessionId: sessionId, status: ReviewStatus.Expired)
            with
        { Amendments = priorAmendments, ResubmittedTo = childId };
        var child = TestDocketEntry.CreateDefault(entryId: childId, sessionId: sessionId);

        await store.FileDocketEntryAsync(parent, CancellationToken.None);
        await store.FileDocketEntryAsync(child, CancellationToken.None);

        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(store, transport);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        var broadcast = Assert.Single(
            transport.Broadcasts,
            b => b.EventType == TransportEvent.EvidenceCardRequest
                 && ((EvidenceCardRequest)b.Payload).DocketId == childId);
        var request = (EvidenceCardRequest)broadcast.Payload;
        Assert.NotNull(request.PriorAmendments);
        Assert.Equal("Edited before expiry", request.PriorAmendments!["title"]);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExpireOverdueAsync_DrainsTheBacklogWithinOneTick_UpToThePerTickCap()
    {
        var origin = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(origin);
        var store = new InMemoryDocketStore(clock);
        var transport = new SpyStreamingTransport();
        var tenantId = Guid.NewGuid().ToString();

        // Seven due entries against a batch of two: the store reports "more remain" after each call
        // and the tick keeps asking until it does not.
        var entries = Enumerable.Range(1, 7)
            .Select(i => TestDocketEntry.CreateDefault(tenantId: tenantId, expiresAt: origin.AddMinutes(i)))
            .ToList();
        foreach (var entry in entries)
            await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var expiryService = BuildExpiryService(
            store,
            transport,
            docketOptions: new AffiantDocketOptions
            {
                ExpirySweepBatchSize = 2,
                ExpirySweepBatchesPerTick = 10,
                SweepScope = new DocketScope(tenantId)
            },
            timeProvider: clock);

        clock.SetUtcNow(origin.AddMinutes(10));
        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        // All seven, in one tick, without any single read having loaded more than two rows.
        Assert.Equal(7, transport.Broadcasts.Count(b => b.EventType == TransportEvent.DocketExpired));
        foreach (var entry in entries)
        {
            var stored = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
            Assert.Equal(ReviewStatus.Expired, stored!.Status);
        }
    }

    [Fact]
    public async Task ExpireOverdueAsync_BacklogLargerThanThePerTickCap_LeavesTheRestForTheNextTick()
    {
        var origin = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(origin);
        var store = new InMemoryDocketStore(clock);
        var transport = new SpyStreamingTransport();
        var tenantId = Guid.NewGuid().ToString();

        var entries = Enumerable.Range(1, 7)
            .Select(i => TestDocketEntry.CreateDefault(tenantId: tenantId, expiresAt: origin.AddMinutes(i)))
            .ToList();
        foreach (var entry in entries)
            await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var expiryService = BuildExpiryService(
            store,
            transport,
            docketOptions: new AffiantDocketOptions
            {
                ExpirySweepBatchSize = 2,
                ExpirySweepBatchesPerTick = 2,
                SweepScope = new DocketScope(tenantId)
            },
            timeProvider: clock);

        clock.SetUtcNow(origin.AddMinutes(10));

        // Two batches of two per tick: a deployment coming back from a long outage drains steadily
        // instead of turning one tick into the unbounded pass the cap exists to prevent.
        await expiryService.ExpireOverdueAsync(CancellationToken.None);
        Assert.Equal(4, transport.Broadcasts.Count(b => b.EventType == TransportEvent.DocketExpired));

        await expiryService.ExpireOverdueAsync(CancellationToken.None);
        Assert.Equal(7, transport.Broadcasts.Count(b => b.EventType == TransportEvent.DocketExpired));

        await expiryService.ExpireOverdueAsync(CancellationToken.None);
        Assert.Equal(7, transport.Broadcasts.Count(b => b.EventType == TransportEvent.DocketExpired));
    }

    private static DocketExpiryService BuildExpiryService(
        IDocketStore store,
        IStreamingTransport? transport = null,
        AffiantCoreOptions? options = null,
        AffiantDocketOptions? docketOptions = null,
        TimeProvider? timeProvider = null)
    {
        // Register the test store as Singleton so the service resolves the same
        // instance that test data was written to — the scope factory is built from
        // a minimal ServiceCollection rather than mocked.
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new DocketExpiryService(
            scopeFactory,
            options ?? new AffiantCoreOptions(),
            NullLogger<DocketExpiryService>.Instance,
            transport,
            docketOptions,
            timeProvider);
    }



    private sealed class SpyStreamingTransport : IStreamingTransport
    {
        public List<(string GroupId, TransportEvent EventType, object Payload)> Broadcasts { get; } = [];

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            Broadcasts.Add((groupId, eventType, payload));
            return Task.CompletedTask;
        }

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new InvalidOperationException("SpyStreamingTransport.SendAsync should not be called by DocketExpiryService");

        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException("SpyStreamingTransport.AwaitEvidenceCardResponseAsync should not be called by DocketExpiryService");
    }

    // ── The sweep is bounded and clock-driven (protocol DK-3) ────────────────

    [Fact]
    public async Task ExpireOverdueAsync_OneBatchPerTick_ExpiresAtMostBatchPerTick_OldestFirst()
    {
        var origin = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(origin);
        var store = new InMemoryDocketStore(clock);

        // Five entries fall due, staggered so "oldest deadline first" is observable.
        var entries = Enumerable.Range(1, 5)
            .Select(i => TestDocketEntry.CreateDefault(expiresAt: origin.AddMinutes(i)))
            .ToList();
        foreach (var entry in entries)
            await store.FileDocketEntryAsync(entry, CancellationToken.None);

        // One call per tick: what the tick expires is then exactly one batch, which is what this
        // test is about. The per-tick cap is the second bound and has its own test below.
        var expiryService = BuildExpiryService(
            store,
            docketOptions: new AffiantDocketOptions
            {
                ExpirySweepBatchSize = 2,
                ExpirySweepBatchesPerTick = 1
            },
            timeProvider: clock);

        clock.SetUtcNow(origin.AddMinutes(10));

        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        var afterFirstTick = await Task.WhenAll(
            entries.Select(async e => (await store.GetDocketEntryAsync(e.EntryId, CancellationToken.None))!));

        // The read-time projection reports every one of them Expired — that is the point of expiry
        // being a queryable state — so what the batch bounds is the PERSISTED transition, which
        // UpdateReviewStatusAsync's Pending guard is the only honest witness to: a row this tick
        // already committed refuses a second transition (0 rows), one it has not yet reached accepts.
        Assert.All(afterFirstTick, e => Assert.Equal(ReviewStatus.Expired, e.Status));

        var committedAfterFirstTick = new List<Guid>();
        foreach (var entry in entries)
        {
            var rows = await store.UpdateReviewStatusAsync(
                entry.EntryId, ReviewStatus.Expired, CancellationToken.None);
            if (rows == 0)
                committedAfterFirstTick.Add(entry.EntryId);
        }

        Assert.Equal([entries[0].EntryId, entries[1].EntryId], committedAfterFirstTick);
    }

    [Fact]
    public async Task ExpireOverdueAsync_BacklogLargerThanTheBatch_DrainsAcrossTicks()
    {
        var origin = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(origin);
        var store = new InMemoryDocketStore(clock);
        var transport = new SpyStreamingTransport();

        var entries = Enumerable.Range(1, 5)
            .Select(i => TestDocketEntry.CreateDefault(expiresAt: origin.AddMinutes(i)))
            .ToList();
        foreach (var entry in entries)
            await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var expiryService = BuildExpiryService(
            store,
            transport,
            docketOptions: new AffiantDocketOptions
            {
                ExpirySweepBatchSize = 2,
                ExpirySweepBatchesPerTick = 1
            },
            timeProvider: clock);

        clock.SetUtcNow(origin.AddMinutes(10));

        // One DocketExpired broadcast per entry the tick's own write actually transitioned, so the
        // broadcast count is the visible count of committed transitions per tick: 2, 2, then 1.
        await expiryService.ExpireOverdueAsync(CancellationToken.None);
        Assert.Equal(2, transport.Broadcasts.Count(b => b.EventType == TransportEvent.DocketExpired));

        await expiryService.ExpireOverdueAsync(CancellationToken.None);
        Assert.Equal(4, transport.Broadcasts.Count(b => b.EventType == TransportEvent.DocketExpired));

        await expiryService.ExpireOverdueAsync(CancellationToken.None);
        Assert.Equal(5, transport.Broadcasts.Count(b => b.EventType == TransportEvent.DocketExpired));

        // A fourth tick has nothing left to do.
        await expiryService.ExpireOverdueAsync(CancellationToken.None);
        Assert.Equal(5, transport.Broadcasts.Count(b => b.EventType == TransportEvent.DocketExpired));

        foreach (var entry in entries)
        {
            var rows = await store.UpdateReviewStatusAsync(
                entry.EntryId, ReviewStatus.Expired, CancellationToken.None);
            Assert.Equal(0, rows);
        }
    }

    [Fact]
    public void AffiantDocketOptions_BatchSizeBelowOne_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AffiantDocketOptions { ExpirySweepBatchSize = 0 });
    }
}
