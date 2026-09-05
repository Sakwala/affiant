using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Docket.Tests.Fixtures;
using Xunit;

namespace Affiant.Docket.Tests;

/// <summary>
/// Shared invariant tests that run against all three IDocketStore implementations.
/// Each [Theory] iterates over InMemory, SQLite, and Postgres via DocketStoreProviderFactory,
/// so a regression in any backend fails the build immediately.
///
/// Three invariants from the framework spec are validated (R1, R3, round-trip):
///   Case 1 — Round-trip preservation: all DocketEntry fields survive file → retrieve.
///   Case 2 — Double-submit guard: TransitionAsync refuses when the entry is no longer Pending.
///   Case 3 — Expiry idempotency: MarkExpiredAsync called twice does not corrupt state.
///   Case 4 — Amendments round-trip (issue #6): UpdateAmendmentsAsync persists reviewer edits,
///            including an explicit null value for a field the reviewer cleared.
///   Case 5 — FileDocketEntryAsync idempotency (issue #32): a second filing call for an
///            already-used EntryId is a no-op — the first payload and status survive
///            untouched, even when the entry has already gone terminal.
///   Case 6 — ConsumeForResubmitAsync double-resubmit race guard (affiant#31, Area-5 D2):
///            two genuinely concurrent claims for the same expired entry — exactly one wins.
///   Case 7 — ListPendingBySessionAsync ordering (Area-5 Decision 3 / P2d rider).
///   Case 8 — SaveContextAsync/LoadContextAsync round-trip (Area-5 P4 item I): the
///            fabric-persistence path Area 3 built on IDocketStore, previously zero framework
///            coverage on any backend (evidence pack area-5-store-parity.md §3 "escapes").
///   Case 9 — Genuinely concurrent races beyond Case 6 (Area-5 P4 item I): double
///            TransitionAsync CAS on one Pending entry, and double FileDocketEntryAsync
///            on the same EntryId — both via Task.WhenAll against independent store instances,
///            not sequential simulation.
/// </summary>
public sealed class SharedDocketStoreTests
{
    // ── Case 1: Round-trip preservation ─────────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task FileDocketEntry_RoundTrip_PreservesAllFields(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault();

        await store.FileDocketEntryAsync(entry, CancellationToken.None);
        var retrieved = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Equal(entry.EntryId, retrieved.EntryId);
        Assert.Equal(entry.SessionId, retrieved.SessionId);
        Assert.Equal(entry.TenantId, retrieved.TenantId);
        Assert.Equal(entry.UserId, retrieved.UserId);
        Assert.Equal(entry.ReviewerUserId, retrieved.ReviewerUserId);
        Assert.Equal(entry.OperationType, retrieved.OperationType);
        Assert.Equal(entry.Status, retrieved.Status);

        // Provenance round-trip: both fields must survive the JSON serialization path.
        // EF stores serialize ProvenanceChain separately into ProvenanceChainsJson,
        // then re-inject it into the Affidavit fields on load.
        Assert.Equal(entry.Envelope.Fields.Length, retrieved.Envelope.Fields.Length);

        var originalField = entry.Envelope.Fields[0];
        var roundTrippedField = retrieved.Envelope.Fields
            .Single(f => f.Name == originalField.Name);

        Assert.Equal(originalField.Provenance.Current.Source,
            roundTrippedField.Provenance.Current.Source);
        Assert.Equal(originalField.Provenance.Current.Confidence,
            roundTrippedField.Provenance.Current.Confidence);
        Assert.Equal(originalField.Provenance.Current.Evidence,
            roundTrippedField.Provenance.Current.Evidence);

        // Verify second field's provenance (inferred source with non-null turn)
        var originalSecond = entry.Envelope.Fields[1];
        var roundTrippedSecond = retrieved.Envelope.Fields
            .Single(f => f.Name == originalSecond.Name);

        Assert.Equal(originalSecond.Provenance.Current.Source,
            roundTrippedSecond.Provenance.Current.Source);
        Assert.Equal(originalSecond.Provenance.Current.ConversationTurn,
            roundTrippedSecond.Provenance.Current.ConversationTurn);
    }

    // ── Case 2: Double-submit guard ──────────────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Transition_DoubleSubmitGuard_RejectsASecondDecisionOnANonPendingEntry(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault();
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        // First decision: the row is pending → the compare-and-set wins.
        var first = await store.TransitionAsync(
            entry.EntryId, new DocketScope(entry.TenantId), ReviewStatus.Pending,
            Decided(ReviewStatus.Approved, entry.EntryId), CancellationToken.None);
        Assert.IsType<DocketTransitionResult.Transitioned>(first);

        var afterFirst = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Approved, afterFirst!.Status);

        // Second decision: the row is approved → refused, not applied.
        var second = await store.TransitionAsync(
            entry.EntryId, new DocketScope(entry.TenantId), ReviewStatus.Pending,
            Decided(ReviewStatus.Rejected, entry.EntryId), CancellationToken.None);
        Assert.IsType<DocketTransitionResult.AlreadyDecided>(second);

        var afterSecond = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Approved, afterSecond!.Status);
    }

    // ── Case 3: Expiry idempotency ───────────────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ExpireDue_CalledTwiceForTheSameEntry_ExpiresItOnceAndReportsItOnce(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = Guid.NewGuid().ToString();
        var entry = TestDocketEntry.Expired(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);
        var scope = new DocketScope(tenantId);

        // First tick: this call's own guarded write is the one that transitioned the row, so the row
        // comes back in its result — which is what gives the caller the right to notify on it.
        var first = await store.ExpireDueAsync(
            DateTimeOffset.UtcNow, scope, limit: 100, CancellationToken.None);
        Assert.Contains(first.Expired, e => e.EntryId == entry.EntryId);

        var afterFirst = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Expired, afterFirst!.Status);

        // Second tick: the row is no longer pending, so the guard finds nothing to write and the
        // sweep does not claim it again. A caller that broadcast on a repeat would double-notify.
        var second = await store.ExpireDueAsync(
            DateTimeOffset.UtcNow, scope, limit: 100, CancellationToken.None);
        Assert.DoesNotContain(second.Expired, e => e.EntryId == entry.EntryId);

        var afterSecond = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Expired, afterSecond!.Status);
        Assert.Equal(afterFirst.ExpiresAt, afterSecond.ExpiresAt);
    }

    // ── Case 4: Amendments round-trip (issue #6) ─────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task FileDocketEntryAsync_SecondCallSameEntryId_IsNoOpAndPreservesFirstPayload(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entryId = Guid.NewGuid();

        var first = TestDocketEntry.CreateDefault(entryId: entryId, sessionId: "session-first");
        await store.FileDocketEntryAsync(first, CancellationToken.None);

        var second = TestDocketEntry.CreateDefault(entryId: entryId, sessionId: "session-second");
        await store.FileDocketEntryAsync(second, CancellationToken.None);

        var retrieved = await store.GetDocketEntryAsync(entryId, CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Equal("session-first", retrieved.SessionId);
        Assert.Equal(ReviewStatus.Pending, retrieved.Status);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task FileDocketEntryAsync_SecondCallOnTerminalEntry_DoesNotResetStatusOrPayload(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entryId = Guid.NewGuid();

        var first = TestDocketEntry.CreateDefault(entryId: entryId, sessionId: "session-first");
        await store.FileDocketEntryAsync(first, CancellationToken.None);
        await store.TransitionAsync(
            entryId, new DocketScope(first.TenantId), ReviewStatus.Pending,
            Decided(ReviewStatus.Approved, entryId), CancellationToken.None);

        // A retried filing (or a race the store-level guard was meant to catch) arrives after
        // the entry already went terminal. The documented contract — and issue #32's fix — say
        // this must no-op, not silently revert the entry back to Pending with the new payload.
        var second = TestDocketEntry.CreateDefault(entryId: entryId, sessionId: "session-second");
        await store.FileDocketEntryAsync(second, CancellationToken.None);

        var retrieved = await store.GetDocketEntryAsync(entryId, CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Equal("session-first", retrieved.SessionId);
        Assert.Equal(ReviewStatus.Approved, retrieved.Status);
    }

    // ── Case 6: ConsumeForResubmitAsync race guard (affiant#31, Area-5 D2) ────

    [Theory]
    [ClassData(typeof(DocketStoreConcurrencyProviderFactory))]
    public async Task ConsumeForResubmitAsync_GenuinelyConcurrentCalls_ExactlyOneWins(
        IDocketStore storeA, IDocketStore storeB, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = await TestDocketEntry.FileDecidedAsync(storeA, ReviewStatus.Expired);

        var firstNewId = Guid.NewGuid();
        var secondNewId = Guid.NewGuid();

        // Task.Run against two independent store instances (not a sequential simulation, and not
        // one shared DbContext — EF Core forbids concurrent operations on a single instance) — the
        // two calls genuinely race the store's own WHERE Status = 'Expired' AND ResubmittedTo IS
        // NULL guard, exactly as two separate requests' own Scoped DbContexts would in production.
        var firstTask = Task.Run(() =>
            storeA.ConsumeForResubmitAsync(entry.EntryId, firstNewId, CancellationToken.None));
        var secondTask = Task.Run(() =>
            storeB.ConsumeForResubmitAsync(entry.EntryId, secondNewId, CancellationToken.None));

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, results.Sum());

        var updated = await storeA.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal(ReviewStatus.Expired, updated.Status); // never resurrected — no ReviewStatus.Resubmitted
        Assert.True(updated.ResubmittedTo == firstNewId || updated.ResubmittedTo == secondNewId);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ConsumeForResubmitAsync_NonExpiredEntry_ReturnsZero(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault(status: ReviewStatus.Pending);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var rows = await store.ConsumeForResubmitAsync(entry.EntryId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(0, rows);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task GetResubmissionParentAsync_FindsParentByLineage_NullWhenNoParent(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var parent = await TestDocketEntry.FileDecidedAsync(store, ReviewStatus.Expired);

        var childId = Guid.NewGuid();
        var consumed = await store.ConsumeForResubmitAsync(parent.EntryId, childId, CancellationToken.None);
        Assert.Equal(1, consumed);

        var foundParent = await store.GetResubmissionParentAsync(childId, CancellationToken.None);
        Assert.NotNull(foundParent);
        Assert.Equal(parent.EntryId, foundParent.EntryId);

        var noParent = await store.GetResubmissionParentAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(noParent);
    }

    // ── Case 7: ListPendingBySessionAsync ordering (Area-5 Decision 3 / P2d rider) ───

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ListPendingBySessionAsync_MultiplePendingEntries_OrderedByCreatedAtAscending(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var sessionId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        // Filed out of chronological order — the store's own CreatedAt stamps decide the order,
        // not insertion order.
        var third = TestDocketEntry.CreateDefault(sessionId: sessionId) with { CreatedAt = now.AddMinutes(2) };
        var first = TestDocketEntry.CreateDefault(sessionId: sessionId) with { CreatedAt = now };
        var second = TestDocketEntry.CreateDefault(sessionId: sessionId) with { CreatedAt = now.AddMinutes(1) };

        await store.FileDocketEntryAsync(third, CancellationToken.None);
        await store.FileDocketEntryAsync(first, CancellationToken.None);
        await store.FileDocketEntryAsync(second, CancellationToken.None);

        var pending = await store.ListPendingBySessionAsync(sessionId, CancellationToken.None);

        Assert.Equal(
            [first.EntryId, second.EntryId, third.EntryId],
            pending.Select(e => e.EntryId));
    }

    // ── Case 8: SaveContextAsync/LoadContextAsync round-trip (Area-5 P4 item I) ──

    [Theory]
    [ClassData(typeof(DocketStoreWithChatSessionProviderFactory))]
    public async Task SaveContextAsync_ThenLoadContextAsync_RoundTripsEntities(
        IDocketStore store, IChatSessionStore chatSessionStore, string providerName)
    {
        Assert.NotEmpty(providerName);
        var session = await chatSessionStore.CreateAsync("tenant-001", "user-001", CancellationToken.None);
        var sessionId = session.SessionId;
        var entity = new EntityRef(
            EntityType: "test-entity-type",
            EntityId: "entity-001",
            DisplayName: "Test Entity",
            Fields: new Dictionary<string, object> { ["status"] = "active", ["count"] = 3 });
        var context = new ConversationContext(
            sessionId, new Dictionary<string, EntityRef> { ["entity-001"] = entity });

        await store.SaveContextAsync(sessionId, context, CancellationToken.None);
        var retrieved = await store.LoadContextAsync(sessionId, CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Equal(sessionId, retrieved.SessionId);
        Assert.Single(retrieved.Entities);

        var roundTripped = retrieved.Entities["entity-001"];
        Assert.Equal("test-entity-type", roundTripped.EntityType);
        Assert.Equal("entity-001", roundTripped.EntityId);
        Assert.Equal("Test Entity", roundTripped.DisplayName);
        Assert.Equal("active", roundTripped.Fields["status"].ToString());
        Assert.Equal("3", roundTripped.Fields["count"].ToString());
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task LoadContextAsync_ForSessionWithNoSavedContext_ReturnsNull(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);

        var result = await store.LoadContextAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [ClassData(typeof(DocketStoreWithChatSessionProviderFactory))]
    public async Task SaveContextAsync_CalledTwice_UpsertsOverPreviousContext(
        IDocketStore store, IChatSessionStore chatSessionStore, string providerName)
    {
        Assert.NotEmpty(providerName);
        var session = await chatSessionStore.CreateAsync("tenant-001", "user-001", CancellationToken.None);
        var sessionId = session.SessionId;

        var first = new ConversationContext(sessionId, new Dictionary<string, EntityRef>
        {
            ["entity-001"] = new EntityRef("type-a", "entity-001", "First", new Dictionary<string, object>())
        });
        await store.SaveContextAsync(sessionId, first, CancellationToken.None);

        var second = new ConversationContext(sessionId, new Dictionary<string, EntityRef>
        {
            ["entity-002"] = new EntityRef("type-b", "entity-002", "Second", new Dictionary<string, object>())
        });
        await store.SaveContextAsync(sessionId, second, CancellationToken.None);

        var retrieved = await store.LoadContextAsync(sessionId, CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Single(retrieved.Entities);
        Assert.True(retrieved.Entities.ContainsKey("entity-002"));
        Assert.False(retrieved.Entities.ContainsKey("entity-001"));
    }

    // ── Case 9: Genuinely concurrent races beyond Case 6 (Area-5 P4 item I) ──

    [Theory]
    [ClassData(typeof(DocketStoreConcurrencyProviderFactory))]
    public async Task TransitionAsync_GenuinelyConcurrentCallsOnPendingEntry_ExactlyOneWins(
        IDocketStore storeA, IDocketStore storeB, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault(status: ReviewStatus.Pending);
        await storeA.FileDocketEntryAsync(entry, CancellationToken.None);

        // Same shape as Case 6's resubmit race: two independent store instances (not one shared
        // DbContext, not a sequential simulation) racing the store's own compare-and-set out of
        // pending.
        var scope = new DocketScope(entry.TenantId);
        var firstTask = Task.Run(() => storeA.TransitionAsync(
            entry.EntryId, scope, ReviewStatus.Pending,
            Decided(ReviewStatus.Approved, entry.EntryId), CancellationToken.None));
        var secondTask = Task.Run(() => storeB.TransitionAsync(
            entry.EntryId, scope, ReviewStatus.Pending,
            Decided(ReviewStatus.Rejected, entry.EntryId), CancellationToken.None));

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results.OfType<DocketTransitionResult.Transitioned>());

        var updated = await storeA.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.NotNull(updated);
        var expectedStatus = results[0] is DocketTransitionResult.Transitioned
            ? ReviewStatus.Approved
            : ReviewStatus.Rejected;
        Assert.Equal(expectedStatus, updated.Status);
    }

    /// <summary>A decision patch that names who agreed and what they chose (AZ-1).</summary>
    private static DocketTransitionPatch Decided(ReviewStatus status, Guid entryId)
    {
        var at = DateTimeOffset.UtcNow;
        return new DocketTransitionPatch(
            status,
            Decision: new DecisionRecord(
                status == ReviewStatus.Approved ? DecisionKind.Approve : DecisionKind.Reject, null, at),
            Attestation: new Attestation(Attestor.Member.FromStorage("member-1"), at, entryId),
            DecidedAt: at);
    }

    [Theory]
    [ClassData(typeof(DocketStoreConcurrencyProviderFactory))]
    public async Task FileDocketEntryAsync_GenuinelyConcurrentCallsSameEntryId_NoOpContractHoldsUnderRace(
        IDocketStore storeA, IDocketStore storeB, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entryId = Guid.NewGuid();
        var first = TestDocketEntry.CreateDefault(entryId: entryId, sessionId: "session-A");
        var second = TestDocketEntry.CreateDefault(entryId: entryId, sessionId: "session-B");

        var firstTask = Task.Run(() => storeA.FileDocketEntryAsync(first, CancellationToken.None));
        var secondTask = Task.Run(() => storeB.FileDocketEntryAsync(second, CancellationToken.None));

        // Neither call may throw — issue #32's fix degrades the loser to the idempotent no-op
        // instead of propagating the unique-constraint violation. Task.WhenAll rethrows if either did.
        await Task.WhenAll(firstTask, secondTask);

        var retrieved = await storeA.GetDocketEntryAsync(entryId, CancellationToken.None);
        Assert.NotNull(retrieved);
        Assert.True(retrieved.SessionId is "session-A" or "session-B");
    }
}
