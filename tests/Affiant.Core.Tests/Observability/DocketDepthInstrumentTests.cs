namespace Affiant.Core.Tests.Observability;

using System.Diagnostics.Metrics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The docket-depth gauge: <c>affiant.docket.pending</c> reports how many entries are awaiting
/// review, without a metrics scrape ever touching the store — and without the refresh loading the
/// Docket to find out (DK-3). It asks the store for a count.
/// </summary>
public class DocketDepthInstrumentTests
{
    [Fact]
    public async Task ReportsHowManyAreAwaitingReview()
    {
        var store = new StubDocketStore();
        store.AddPending("tenant-a", 3);
        store.AddPending("tenant-b", 1);

        using var instrument = new DocketDepthInstrument(
            Scopes(store), NullLogger<DocketDepthInstrument>.Instance);
        await instrument.StartAsync(CancellationToken.None);

        Assert.Equal(4, await CollectWhenSampledAsync(instrument));
    }

    /// <summary>
    /// DK-3: the refresh asks for a count and never for the rows. The store this gauge is pointed
    /// at refuses the unpaged listing outright, so a gauge that went back to loading the Docket
    /// fails here rather than quietly costing a host every pending row every fifteen seconds.
    /// </summary>
    [Fact]
    public async Task TheRefreshAsksForACount_NeverForTheRows()
    {
        var store = new StubDocketStore();
        store.AddPending("tenant-a", 25);

        using var instrument = new DocketDepthInstrument(
            Scopes(store), NullLogger<DocketDepthInstrument>.Instance);
        await instrument.StartAsync(CancellationToken.None);

        Assert.Equal(25, await CollectWhenSampledAsync(instrument));
        Assert.Equal(1, store.Counts);
        Assert.Equal(0, store.UnpagedListings);
    }

    [Fact]
    public async Task ApprovedAndExpiredEntries_AreNotDepth()
    {
        var store = new StubDocketStore();
        store.AddPending("tenant-a", 2);
        store.Add("tenant-a", ReviewStatus.Approved);
        store.Add("tenant-a", ReviewStatus.Expired);

        using var instrument = new DocketDepthInstrument(
            Scopes(store), NullLogger<DocketDepthInstrument>.Instance);
        await instrument.StartAsync(CancellationToken.None);

        Assert.Equal(2, await CollectWhenSampledAsync(instrument));
    }

    [Fact]
    public async Task NoDocketStoreRegistered_ReportsNothing_AndDoesNotThrow()
    {
        using var instrument = new DocketDepthInstrument(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DocketDepthInstrument>.Instance);
        await instrument.StartAsync(CancellationToken.None);

        // No store means no sample will ever land, so this waits for the refresh to complete rather
        // than for a measurement — the assertion is that collection stays empty and quiet.
        await Task.Delay(200);
        Assert.Null(Collect(instrument));
    }

    [Fact]
    public async Task AStoreThatThrows_LeavesTheLastSampleStanding()
    {
        var store = new StubDocketStore();
        store.AddPending("tenant-a", 4);

        using var instrument = new DocketDepthInstrument(
            Scopes(store), NullLogger<DocketDepthInstrument>.Instance);
        await instrument.StartAsync(CancellationToken.None);
        Assert.Equal(4, await CollectWhenSampledAsync(instrument));

        store.FailNextListing = true;
        await Task.Delay(50);

        // The gauge keeps reporting what it last knew rather than dropping to zero, which would read
        // on a dashboard as "the backlog cleared" when what actually happened is "the store is down".
        Assert.Equal(4, Collect(instrument));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static IServiceScopeFactory Scopes(IDocketStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    /// Collects the gauge, retrying until the instrument's background refresh has landed a sample.
    /// Sampling is deliberately asynchronous — a scrape never blocks on the store — so a test that
    /// read the gauge once would be racing the very design it is checking.
    /// </summary>
    private static async Task<long?> CollectWhenSampledAsync(DocketDepthInstrument instrument)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (Collect(instrument) is { } depth) return depth;
            await Task.Delay(20);
        }

        return Collect(instrument);
    }

    private static long? Collect(DocketDepthInstrument instrument)
    {
        long? collected = null;

        using var listener = new MeterListener
        {
            // By instrument reference, not by name: any number of gauges named
            // affiant.docket.pending may be alive in this process (one per instrument another test
            // constructed), and a listener that matched on the name would collect theirs too.
            InstrumentPublished = (published, active) =>
            {
                if (ReferenceEquals(published, instrument.Gauge)) active.EnableMeasurementEvents(published);
            },
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => collected = measurement);

        listener.Start();
        listener.RecordObservableInstruments();
        return collected;
    }

    private sealed class StubDocketStore : IDocketStore
    {
        private readonly List<DocketEntry> _entries = [];

        public bool FailNextListing { get; set; }

        public void AddPending(string tenantId, int count)
        {
            for (var i = 0; i < count; i++) Add(tenantId, ReviewStatus.Pending);
        }

        public void Add(string tenantId, ReviewStatus status) => _entries.Add(new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: "session",
            TenantId: tenantId,
            UserId: "user",
            ReviewerUserId: null,
            OperationType: "CreateOrder",
            Envelope: null!,
            Status: status,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            Amendments: null));

        /// <summary>How many times the gauge asked for a count.</summary>
        public int Counts { get; private set; }

        /// <summary>How many times anything asked this store for every pending row.</summary>
        public int UnpagedListings { get; private set; }

        public Task<long> CountPendingAsync(CancellationToken ct)
        {
            Counts++;
            if (FailNextListing) throw new InvalidOperationException("the store is unavailable");
            return Task.FromResult(_entries.LongCount(e => e.Status == ReviewStatus.Pending));
        }

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
        {
            UnpagedListings++;
            if (FailNextListing) throw new InvalidOperationException("the store is unavailable");
            return Task.FromResult<IReadOnlyList<DocketEntry>>(
                [.. _entries.Where(e => e.Status == ReviewStatus.Pending)]);
        }

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => Task.CompletedTask;

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<ConversationContext?>(null);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct) => Task.CompletedTask;

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);


        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);


        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct) => Task.CompletedTask;
    
        // ── The scoped, guarded, paged surface ──────────────────────────────
        // Explicit implementations that refuse: this double exists for a test that never reaches the
        // Docket's decision surface, and a stub that quietly answered would let such a test pass
        // against behaviour nobody wrote.
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

        Task<int> IDocketStore.MarkBlockedAsync(Guid entryId, DocketScope scope, BlockedMarker marker, CancellationToken ct)
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
