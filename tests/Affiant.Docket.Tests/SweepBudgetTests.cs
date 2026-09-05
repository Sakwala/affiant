namespace Affiant.Docket.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Docket.Options;
using Affiant.Docket.Services;
using Affiant.Docket.Stores;
using Affiant.Docket.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

/// <summary>
/// The sweep's budget covers the whole tick, not only its first phase (DK-3).
/// </summary>
/// <remarks>
/// The class documentation says a tick touches at most
/// <c>ExpirySweepBatchSize × ExpirySweepBatchesPerTick</c> rows. It used to be true of the due
/// queue alone: the expiry-warning and Evidence-Card-re-broadcast phases each walked every pending
/// row in the whole store, across every tenant, on every tick.
/// </remarks>
public sealed class SweepBudgetTests
{
    [Fact]
    public async Task ATickRebroadcastsAtMostItsBudget_NotEveryPendingRow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var store = new InMemoryDocketStore(clock);

        for (var i = 0; i < 25; i++)
        {
            await store.FileDocketEntryAsync(
                TestDocketEntry.CreateDefault(
                    tenantId: $"tenant-{i}",
                    sessionId: $"session-{i}",
                    expiresAt: clock.GetUtcNow().AddHours(1)),
                CancellationToken.None);
        }

        var transport = new CountingTransport();
        var sweep = Build(store, transport, clock, batchSize: 2, batchesPerTick: 1);

        await sweep.ExpireOverdueAsync(CancellationToken.None);

        Assert.Equal(2, transport.Cards);
    }

    [Fact]
    public async Task TheNextTickCarriesOnFromWhereTheLastOneStopped()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var store = new InMemoryDocketStore(clock);

        for (var i = 0; i < 6; i++)
        {
            await store.FileDocketEntryAsync(
                TestDocketEntry.CreateDefault(
                    tenantId: "tenant-a",
                    sessionId: $"session-{i}",
                    createdAt: clock.GetUtcNow().AddSeconds(i),
                    expiresAt: clock.GetUtcNow().AddHours(1)),
                CancellationToken.None);
        }

        var transport = new CountingTransport();
        var sweep = Build(store, transport, clock, batchSize: 2, batchesPerTick: 1);

        await sweep.ExpireOverdueAsync(CancellationToken.None);
        var first = transport.Sessions.ToArray();

        await sweep.ExpireOverdueAsync(CancellationToken.None);
        var second = transport.Sessions.Skip(first.Length).ToArray();

        // A phase that restarted every tick would re-walk the same two rows for ever, and a row past
        // the budget would never be reached.
        Assert.Equal(2, first.Length);
        Assert.Equal(2, second.Length);
        Assert.Empty(first.Intersect(second, StringComparer.Ordinal));
    }

    /// <summary>
    /// DK-3: the Evidence Card re-broadcast is never starved by the warning phase in front of it.
    /// </summary>
    /// <remarks>
    /// One budget spent in phase order made this impossible to satisfy. The warning phase spends for
    /// every pending row it <em>walks</em>, whether or not it warns about anything, so with a
    /// pending set larger than the budget and the default two-minute warning window, phase three got
    /// nothing on every tick for ever and its cursor never advanced. The shipped budget test hid it
    /// by turning the warning phase off.
    /// </remarks>
    [Fact]
    public async Task Phase3_IsNotStarved_UnderTheDefaultWarningWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var store = new InMemoryDocketStore(clock);

        // Six pending rows, none inside the warning window: the warning phase walks them and warns
        // about none of them.
        for (var i = 0; i < 6; i++)
        {
            await store.FileDocketEntryAsync(
                TestDocketEntry.CreateDefault(
                    tenantId: "tenant-a",
                    sessionId: $"session-{i}",
                    createdAt: clock.GetUtcNow().AddSeconds(i),
                    expiresAt: clock.GetUtcNow().AddHours(1)),
                CancellationToken.None);
        }

        var transport = new CountingTransport();
        var sweep = Build(
            store, transport, clock, batchSize: 2, batchesPerTick: 1,
            warningWindow: TimeSpan.FromMinutes(2));

        await sweep.ExpireOverdueAsync(CancellationToken.None);

        Assert.Equal(0, transport.Warnings);
        Assert.Equal(2, transport.Cards);
    }

    private static DocketExpiryService Build(
        InMemoryDocketStore store, IStreamingTransport transport, TimeProvider clock,
        int batchSize, int batchesPerTick, TimeSpan? warningWindow = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDocketStore>(store);
        return new DocketExpiryService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new AffiantCoreOptions { DocketExpiryWarningWindow = warningWindow ?? TimeSpan.Zero },
            NullLogger<DocketExpiryService>.Instance,
            transport,
            new AffiantDocketOptions
            {
                ExpirySweepBatchSize = batchSize,
                ExpirySweepBatchesPerTick = batchesPerTick,
            },
            clock);
    }

    private sealed class CountingTransport : IStreamingTransport
    {
        public int Cards { get; private set; }

        public int Warnings { get; private set; }

        public List<string> Sessions { get; } = [];

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            if (eventType == TransportEvent.EvidenceCardRequest)
            {
                Cards++;
                Sessions.Add(groupId);
            }
            else if (eventType == TransportEvent.DocketExpiring)
            {
                Warnings++;
            }

            return Task.CompletedTask;
        }

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => Task.FromCanceled<DecisionHandOff>(new CancellationToken(canceled: true));
    }
}
