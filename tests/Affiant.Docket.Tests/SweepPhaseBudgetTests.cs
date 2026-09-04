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
/// DK-3: each of the sweep's three phases has its own per-tick budget and its own cursor, so a
/// saturated due queue cannot take the whole tick and leave the expiry warnings and the Evidence
/// Card re-broadcast with nothing — the starvation these tests exist to catch, one phase earlier
/// than where it was first found.
/// </summary>
public sealed class SweepPhaseBudgetTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-04T09:00:00Z");

    /// <summary>
    /// Budget 2, thirty pending rows, none of them due. Both later phases must progress across
    /// ticks, and no tick may touch more than three times the budget.
    /// </summary>
    [Fact]
    public async Task SaturatedPendingSet_Phase2And3BothProgressAcrossTicks()
    {
        var clock = new FakeTimeProvider(T0);
        var store = new InMemoryDocketStore(clock);

        // 30 rows: the first 15 sit inside a 2-minute warning window so phase 2 has something to do,
        // the rest are an hour out. None is past its deadline, so phase 1 spends nothing.
        for (var i = 0; i < 30; i++)
        {
            await store.FileDocketEntryAsync(
                TestDocketEntry.CreateDefault(
                    tenantId: "tenant-a",
                    sessionId: $"s-{i:00}",
                    createdAt: T0.AddSeconds(i),
                    expiresAt: i < 15 ? T0.AddSeconds(60) : T0.AddHours(1)),
                CancellationToken.None);
        }

        var transport = new CountingTransport();
        var sweep = Build(store, transport, clock, 2, 1, TimeSpan.FromMinutes(2));

        var cardsPerTick = new List<int>();
        var warnedSessions = new HashSet<string>(StringComparer.Ordinal);
        var cardSessions = new HashSet<string>(StringComparer.Ordinal);

        for (var tick = 0; tick < 8; tick++)
        {
            var before = transport.Total;
            await sweep.ExpireOverdueAsync(CancellationToken.None);
            cardsPerTick.Add(transport.Total - before);
            foreach (var s in transport.WarnSessions) warnedSessions.Add(s);
            foreach (var s in transport.CardSessions) cardSessions.Add(s);
        }

        // Budget = 2 x 1 = 2 per phase; a tick touches at most three times that.
        Assert.All(cardsPerTick, n => Assert.True(n <= 6, $"a tick touched {n} rows, budget x3 = 6"));
        // Phase 3 advanced past the first page rather than re-walking it.
        Assert.True(cardSessions.Count >= 8, $"phase 3 reached only {cardSessions.Count} distinct rows in 8 ticks");
        // Phase 2 advanced too.
        Assert.True(warnedSessions.Count >= 8, $"phase 2 warned only {warnedSessions.Count} distinct rows in 8 ticks");
    }

    /// <summary>
    /// A saturated DUE queue: ten rows past their deadline and ten healthy ones. Draining the due
    /// queue is phase 1's own bounded work and must not be charged to the two phases behind it.
    /// </summary>
    [Fact]
    public async Task ASaturatedDueQueue_DoesNotStarveTheWarningsOrTheRebroadcast()
    {
        var clock = new FakeTimeProvider(T0);
        var store = new InMemoryDocketStore(clock);

        // 10 rows already past their deadline (the due queue), plus 10 healthy pending rows that
        // phase 2 should warn about and phase 3 should re-broadcast.
        for (var i = 0; i < 10; i++)
        {
            await store.FileDocketEntryAsync(
                TestDocketEntry.CreateDefault(
                    tenantId: "tenant-a", sessionId: $"due-{i:00}",
                    createdAt: T0.AddSeconds(-3600), expiresAt: T0.AddSeconds(-60)),
                CancellationToken.None);
        }

        for (var i = 0; i < 10; i++)
        {
            await store.FileDocketEntryAsync(
                TestDocketEntry.CreateDefault(
                    tenantId: "tenant-a", sessionId: $"live-{i:00}",
                    createdAt: T0.AddSeconds(i), expiresAt: T0.AddSeconds(60)),
                CancellationToken.None);
        }

        var transport = new CountingTransport();
        var sweep = Build(store, transport, clock, 2, 1, TimeSpan.FromMinutes(2));

        await sweep.ExpireOverdueAsync(CancellationToken.None);

        // What the class doc promises: phases 2 and 3 have their own budget, so they run.
        Assert.True(transport.Cards > 0,
            "phase 3 got no budget at all: phase 1 spent the local the later phases are handed");
        Assert.True(transport.Warnings > 0,
            "phase 2 got no budget at all");
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

        public int Total => Cards + Warnings;

        public List<string> CardSessions { get; } = [];

        public List<string> WarnSessions { get; } = [];

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            if (eventType == TransportEvent.EvidenceCardRequest)
            {
                Cards++;
                CardSessions.Add(groupId);
            }
            else if (eventType == TransportEvent.DocketExpiring)
            {
                Warnings++;
                WarnSessions.Add(groupId);
            }

            return Task.CompletedTask;
        }

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => Task.FromCanceled<DecisionHandOff>(new CancellationToken(canceled: true));
    }
}
