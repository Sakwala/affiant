namespace Affiant.Core.Tests.Observability;

using System.Diagnostics.Metrics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The docket-depth gauge (repo issue #66): <c>affiant.docket.pending</c> reports how many entries
/// are awaiting review, per tenant, without a metrics scrape ever touching the store.
/// </summary>
public class DocketDepthInstrumentTests
{
    [Fact]
    public async Task ReportsPendingEntries_PerTenant()
    {
        var store = new StubDocketStore();
        store.AddPending("tenant-a", 3);
        store.AddPending("tenant-b", 1);

        using var instrument = new DocketDepthInstrument(
            Scopes(store), NullLogger<DocketDepthInstrument>.Instance);
        await instrument.StartAsync(CancellationToken.None);

        var measurements = await CollectWhenSampledAsync(instrument);

        Assert.Equal(3, measurements["tenant-a"]);
        Assert.Equal(1, measurements["tenant-b"]);
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

        var measurements = await CollectWhenSampledAsync(instrument);

        Assert.Equal(2, measurements["tenant-a"]);
    }

    /// <summary>
    /// The cardinality bound is the whole reason this gauge is safe to ship: a host with more
    /// tenants than <see cref="DocketDepthInstrument.MaxTenantSeries"/> gets the deepest queues as
    /// their own series and the rest summed into one, so a collector's series count cannot track a
    /// host's tenant count.
    /// </summary>
    [Fact]
    public async Task TenantSeries_AreBounded_AndTheTailIsSummed()
    {
        var store = new StubDocketStore();
        var overflowTenants = 25;
        for (var i = 0; i < DocketDepthInstrument.MaxTenantSeries + overflowTenants; i++)
        {
            // The first MaxTenantSeries tenants each get two entries so they outrank the tail,
            // which gets one each and folds into the overflow series.
            store.AddPending($"tenant-{i:D4}", i < DocketDepthInstrument.MaxTenantSeries ? 2 : 1);
        }

        using var instrument = new DocketDepthInstrument(
            Scopes(store), NullLogger<DocketDepthInstrument>.Instance);
        await instrument.StartAsync(CancellationToken.None);

        var measurements = await CollectWhenSampledAsync(instrument);

        Assert.Equal(DocketDepthInstrument.MaxTenantSeries + 1, measurements.Count);
        Assert.Equal(overflowTenants, measurements[DocketDepthInstrument.OverflowTenantId]);
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
        Assert.Empty(Collect(instrument));
    }

    [Fact]
    public async Task AStoreThatThrows_LeavesTheLastSampleStanding()
    {
        var store = new StubDocketStore();
        store.AddPending("tenant-a", 4);

        using var instrument = new DocketDepthInstrument(
            Scopes(store), NullLogger<DocketDepthInstrument>.Instance);
        await instrument.StartAsync(CancellationToken.None);
        var first = await CollectWhenSampledAsync(instrument);
        Assert.Equal(4, first["tenant-a"]);

        store.FailNextListing = true;
        await Task.Delay(50);

        // The gauge keeps reporting what it last knew rather than dropping to zero, which would read
        // on a dashboard as "the backlog cleared" when what actually happened is "the store is down".
        Assert.Equal(4, Collect(instrument)["tenant-a"]);
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
    private static async Task<Dictionary<string, long>> CollectWhenSampledAsync(DocketDepthInstrument instrument)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var measurements = Collect(instrument);
            if (measurements.Count > 0) return measurements;
            await Task.Delay(20);
        }

        return Collect(instrument);
    }

    private static Dictionary<string, long> Collect(DocketDepthInstrument instrument)
    {
        var collected = new Dictionary<string, long>(StringComparer.Ordinal);

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

        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == DocketDepthInstrument.TenantTag)
                    collected[(string)tag.Value!] = measurement;
            }
        });

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

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
        {
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

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
            => Task.FromResult(0);

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);

        public Task UpdateAmendmentsAsync(Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
            => Task.CompletedTask;

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
