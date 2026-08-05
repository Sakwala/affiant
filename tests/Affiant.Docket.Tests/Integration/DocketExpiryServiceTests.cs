using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Docket.Services;
using Affiant.Docket.Stores;
using Affiant.Docket.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

        var expiredEntry1 = TestDocketEntry.CreateDefault(expiresAt: now.AddSeconds(-5));
        var expiredEntry2 = TestDocketEntry.CreateDefault(expiresAt: now.AddSeconds(-10));
        var notYetExpired = TestDocketEntry.CreateDefault(expiresAt: now.AddMinutes(5));

        await store.FileDocketEntryAsync(expiredEntry1, CancellationToken.None);
        await store.FileDocketEntryAsync(expiredEntry2, CancellationToken.None);
        await store.FileDocketEntryAsync(notYetExpired, CancellationToken.None);

        var expiryService = BuildExpiryService(store);

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

    // ── Finding 1b regression: DocketExpired must not broadcast for an entry that ──
    // ── did not actually transition to Expired (sweep list-then-mark race) ─────────

    [Fact]
    public async Task ExpireOverdueAsync_EntryApprovedBetweenListAndMark_NoDocketExpiredForIt_SiblingStillNotified()
    {
        var inner = new InMemoryDocketStore();
        var sessionId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        var racer = TestDocketEntry.CreateDefault(sessionId: sessionId, expiresAt: now.AddSeconds(-5));
        var sibling = TestDocketEntry.CreateDefault(sessionId: sessionId, expiresAt: now.AddSeconds(-10));
        await inner.FileDocketEntryAsync(racer, CancellationToken.None);
        await inner.FileDocketEntryAsync(sibling, CancellationToken.None);

        // Wraps the store to inject a concurrent approval of `racer` right after the sweep's
        // ListExpiredAsync snapshot is taken but before MarkExpiredAsync runs — the exact window
        // Finding 1b targets.
        var racyStore = new RaceInjectingDocketStore(inner, racer.EntryId);
        var transport = new SpyStreamingTransport();
        var expiryService = BuildExpiryService(racyStore, transport);

        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        // racer was approved mid-sweep: MarkExpiredAsync's WHERE Status = 'Pending' guard leaves
        // it Approved, and no DocketExpired should have been broadcast for it.
        Assert.DoesNotContain(
            transport.Broadcasts,
            b => b.EventType == TransportEvent.DocketExpired
                 && ((DocketExpiredNotification)b.Payload).DocketId == racer.EntryId);

        var racerEntry = await inner.GetDocketEntryAsync(racer.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Approved, racerEntry!.Status);

        // sibling was genuinely overdue throughout and still gets its DocketExpired broadcast.
        var siblingBroadcast = Assert.Single(
            transport.Broadcasts,
            b => b.EventType == TransportEvent.DocketExpired
                 && ((DocketExpiredNotification)b.Payload).DocketId == sibling.EntryId);
        Assert.Equal(sessionId, siblingBroadcast.GroupId);

        var siblingEntry = await inner.GetDocketEntryAsync(sibling.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Expired, siblingEntry!.Status);
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

    private static DocketExpiryService BuildExpiryService(
        IDocketStore store, IStreamingTransport? transport = null, AffiantCoreOptions? options = null)
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
            transport);
    }

    /// <summary>
    /// Decorates an <see cref="IDocketStore"/> to inject a concurrent status transition on
    /// <paramref name="racingEntryId"/> immediately after the first <see cref="ListExpiredAsync"/>
    /// call returns — simulating a reviewer decision (e.g. via <c>HandleDecisionAsync</c>'s restart
    /// path) landing in the window between the sweep's list snapshot and its bulk mark.
    /// </summary>
    private sealed class RaceInjectingDocketStore(IDocketStore inner, Guid racingEntryId) : IDocketStore
    {
        private bool _raced;

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => inner.SaveContextAsync(sessionId, context, ct);

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => inner.LoadContextAsync(sessionId, ct);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
            => inner.FileDocketEntryAsync(entry, ct);

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => inner.GetDocketEntryAsync(entryId, ct);

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
            => inner.UpdateReviewStatusAsync(entryId, status, ct);

        public Task<int> TryConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => inner.TryConsumeForResubmitAsync(entryId, newEntryId, ct);

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => inner.GetResubmissionParentAsync(entryId, ct);

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
            => inner.UpdateAmendmentsAsync(entryId, amendments, ct);

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => inner.ListPendingBySessionAsync(sessionId, ct);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => inner.ListAllPendingAsync(ct);

        public async Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(
            DateTimeOffset expiresBeforeUtc, CancellationToken ct)
        {
            var result = await inner.ListExpiredAsync(expiresBeforeUtc, ct);
            if (!_raced)
            {
                _raced = true;
                await inner.UpdateReviewStatusAsync(racingEntryId, ReviewStatus.Approved, ct);
            }

            return result;
        }

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
            => inner.MarkExpiredAsync(entryIds, ct);
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
}
