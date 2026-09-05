namespace Affiant.Docket.Tests.Integration;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Telemetry;
using Affiant.Core.Extensions;
using Affiant.Core.Observability;
using Affiant.Docket.Services;
using Affiant.Docket.Stores;
using Affiant.Docket.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The expiry registry event (TL-1, DK-3): one <c>docket.expired</c> per entry whose expiry was
/// actually recorded, and none for an entry that was merely looked at.
/// </summary>
/// <remarks>
/// The event is emitted where the expiry is RECORDED — the store — and not where the sweep happens
/// to be scheduled from. DK-3 explicitly sanctions a host scheduling the sweep itself and calling
/// <c>IDocketStore.ExpireDueAsync</c> directly; when the hosted service was the only emitter, such a
/// host recorded its expiries durably and emitted nothing, so an operator counting expiries saw a
/// number that depended on which of two supported wirings the host had chosen.
/// </remarks>
public sealed class DocketExpiryTelemetryTests
{
    [Fact]
    public async Task TheSweep_EmitsOneDocketExpired_PerEntryItExpires()
    {
        var store = new InMemoryDocketStore();
        var now = DateTimeOffset.UtcNow;
        var overdueOne = TestDocketEntry.CreateDefault(expiresAt: now.AddSeconds(-5));
        var overdueTwo = TestDocketEntry.CreateDefault(expiresAt: now.AddSeconds(-10));
        var stillLive = TestDocketEntry.CreateDefault(expiresAt: now.AddMinutes(5));

        await store.FileDocketEntryAsync(overdueOne, CancellationToken.None);
        await store.FileDocketEntryAsync(overdueTwo, CancellationToken.None);
        await store.FileDocketEntryAsync(stillLive, CancellationToken.None);

        using var probe = new TelemetryProbe();
        await BuildExpiryService(store).ExpireOverdueAsync(CancellationToken.None);

        var expired = probe.Events
            .Where(e => e.Name == TelemetryKeys.DocketExpired)
            .Select(e => (string)e.Tags.Single(t => t.Key == TelemetryKeys.Attributes.EntryId).Value!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>([overdueOne.EntryId.ToString(), overdueTwo.EntryId.ToString()], StringComparer.Ordinal),
            expired);
    }

    /// <summary>
    /// A second tick over the same entries writes nothing, so it must say nothing. An event per
    /// tick rather than per transition would turn a 30-second sweep into a permanent, and entirely
    /// false, stream of expiries.
    /// </summary>
    [Fact]
    public async Task ASecondTick_OverAlreadyExpiredEntries_EmitsNothing()
    {
        var store = new InMemoryDocketStore();
        await store.FileDocketEntryAsync(
            TestDocketEntry.CreateDefault(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5)),
            CancellationToken.None);

        var service = BuildExpiryService(store);
        await service.ExpireOverdueAsync(CancellationToken.None);

        using var probe = new TelemetryProbe();
        await service.ExpireOverdueAsync(CancellationToken.None);

        Assert.DoesNotContain(probe.Events, e => e.Name == TelemetryKeys.DocketExpired);
    }

    /// <summary>
    /// A host that schedules the sweep itself — no hosted service anywhere — still emits the event,
    /// because the store is what records the expiry (DK-3).
    /// </summary>
    [Fact]
    public async Task AHostSchedulingTheSweepItself_EmitsTheSameEvent()
    {
        var store = new InMemoryDocketStore();
        var overdue = TestDocketEntry.CreateDefault(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));
        await store.FileDocketEntryAsync(overdue, CancellationToken.None);

        using var probe = new TelemetryProbe();
        await store.ExpireDueAsync(
            DateTimeOffset.UtcNow, DocketScope.EntireStore, 10, CancellationToken.None);

        var expired = probe.Events
            .Where(e => e.Name == TelemetryKeys.DocketExpired)
            .Select(e => (string)e.Tags.Single(t => t.Key == TelemetryKeys.Attributes.EntryId).Value!)
            .ToList();

        Assert.Equal([overdue.EntryId.ToString()], expired);
    }

    /// <summary>One event per expiry, whichever wiring drove it — never one from each.</summary>
    [Fact]
    public async Task TheHostedSweep_EmitsOneEventPerExpiry_NotTwo()
    {
        var store = new InMemoryDocketStore();
        var overdue = TestDocketEntry.CreateDefault(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));
        await store.FileDocketEntryAsync(overdue, CancellationToken.None);

        using var probe = new TelemetryProbe();
        await BuildExpiryService(store).ExpireOverdueAsync(CancellationToken.None);

        Assert.Single(probe.Events, e => e.Name == TelemetryKeys.DocketExpired);
    }

    private static DocketExpiryService BuildExpiryService(IDocketStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var provider = services.BuildServiceProvider();
        return new DocketExpiryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AffiantCoreOptions(),
            NullLogger<DocketExpiryService>.Instance);
    }

    /// <summary>
    /// The same isolated listener the Core suite uses — see its copy for why the source is touched
    /// before the listener is registered (repo issue #17).
    /// </summary>
    private sealed class TelemetryProbe : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Activity? _root;

        public TelemetryProbe()
        {
            var source = AffiantTelemetry.AffiantActivitySource;

            _listener = new ActivityListener
            {
                ShouldListenTo = candidate => ReferenceEquals(candidate, source),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(_listener);
            _root = source.StartActivity("test_root");
        }

        public IReadOnlyList<ActivityEvent> Events => _root?.Events.ToList() ?? [];

        public void Dispose()
        {
            _root?.Dispose();
            _listener.Dispose();
        }
    }
}
