using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Affiant.Docket.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Affiant.Docket.Services;

/// <summary>
/// The host-side scheduler for the Docket's expiry sweep: it drains
/// <see cref="IDocketStore.ExpireDueAsync"/> in bounded batches, warns the UI as entries approach
/// their deadline, and re-broadcasts every still-pending entry's Evidence Card.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class owns a schedule; the store owns the sweep.</b> Every decision about what expires is
/// the store's — which rows are due, in what order, how many at a time and whether more remain —
/// and this type does nothing but call it until the store says there are no more, or until a tick's
/// own cap is reached. That division is the point: a framework package that owned the expiry logic
/// would make expiry depend on this background service running, and expiry is a <em>state</em>, not
/// an event. A row past its deadline reads expired whether or not this service has ever started. What
/// the sweep adds is the durable transition, the notification, and the persisted state a resubmission
/// tests.
/// </para>
/// <para>
/// <b>A tick is bounded twice.</b> Each call to the store takes at most
/// <see cref="AffiantDocketOptions.ExpirySweepBatchSize"/> rows, and a tick makes at most
/// <see cref="AffiantDocketOptions.ExpirySweepBatchesPerTick"/> such calls. A backlog larger than the
/// product drains across ticks rather than turning one tick into an unbounded pass — and nothing is
/// lost in the meantime, because of the paragraph above.
/// </para>
/// <para>
/// <paramref name="transport"/> is optional — the Affiant.Docket package must not hard-require a
/// transport dependency. Hosts that register an <see cref="IStreamingTransport"/> get expiry
/// notifications; hosts that do not simply skip the broadcast half of each tick.
/// </para>
/// <para>
/// A host that would rather schedule the sweep itself — a serverless deployment with no long-lived
/// process, a cron entry, a queue worker — does not register this service at all and calls
/// <see cref="IDocketStore.ExpireDueAsync"/> on its own cadence. Nothing else in the framework
/// depends on this type existing.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Resolves a fresh <see cref="IDocketStore"/> per tick.</param>
/// <param name="options">Core options — the expiry warning window is read from here.</param>
/// <param name="logger">Tick diagnostics.</param>
/// <param name="transport">Optional; see the remarks.</param>
/// <param name="docketOptions">
/// The sweep's own knobs — the per-call batch size and the per-tick cap. Defaults to
/// <see cref="AffiantDocketOptions"/>'s own defaults when a host registered none.
/// </param>
/// <param name="timeProvider">
/// The clock each tick's <c>now</c> and the tick interval itself are driven from. Defaults to
/// <see cref="TimeProvider.System"/>; a test substitutes a fake and advances time by hand instead
/// of waiting 30 real seconds.
/// </param>
public sealed class DocketExpiryService(
    IServiceScopeFactory scopeFactory,
    AffiantCoreOptions options,
    ILogger<DocketExpiryService> logger,
    IStreamingTransport? transport = null,
    AffiantDocketOptions? docketOptions = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly AffiantDocketOptions _docketOptions = docketOptions ?? new AffiantDocketOptions();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _time);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ExpireOverdueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DocketExpiryService tick failed");
            }
        }
    }

    /// <summary>
    /// Runs one expiry tick. Public for testability — integration tests call this directly instead of
    /// waiting for the background timer.
    /// </summary>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// Three independent phases run each tick.
    /// <list type="number">
    /// <item><description>
    /// The store expires due entries in batches until it reports no more remain or this tick's cap is
    /// reached, and returns the rows <em>its own</em> guarded write transitioned. A row a concurrent
    /// decision claimed a beat earlier is not in that list and is not broadcast here, because that
    /// caller owns the notification — which is why the store reports per row rather than as a bulk
    /// count.
    /// </description></item>
    /// <item><description>
    /// Entries still pending but within <see cref="AffiantCoreOptions.DocketExpiryWarningWindow"/> of
    /// their deadline get an expiring warning. This set is re-read every tick, so a warning repeats
    /// while the entry remains inside the window — clients treat repeated warnings for the same entry
    /// as idempotent (key a countdown off the notification's deadline, not off a count of
    /// notifications).
    /// </description></item>
    /// <item><description>
    /// Every entry still pending after phase 1 gets its Evidence Card re-broadcast, unconditionally —
    /// at-least-once by construction, because a group send to zero connected members completes
    /// successfully and so cannot tell anyone whether a card was delivered. This closes
    /// redelivery-until-acted; it does not prove a human saw the card.
    /// </description></item>
    /// </list>
    /// </remarks>
    public async Task ExpireOverdueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocketStore>();

        var now = _time.GetUtcNow();
        var batchSize = _docketOptions.ExpirySweepBatchSize;
        var sweepScope = _docketOptions.SweepScope;

        // Phase 1: drain the due queue in bounded batches.
        var expiredCount = 0;
        for (var batch = 0; batch < _docketOptions.ExpirySweepBatchesPerTick; batch++)
        {
            var result = await store.ExpireDueAsync(now, sweepScope, batchSize, ct);

            foreach (var entry in result.Expired)
            {
                expiredCount++;
                if (transport is not null)
                {
                    await transport.BroadcastToGroupAsync(
                        entry.SessionId, TransportEvent.DocketExpired,
                        new DocketExpiredNotification(entry.EntryId), ct);
                }
            }

            if (!result.More) break;
        }

        if (expiredCount > 0)
            logger.LogInformation("Marked {Count} docket entries as expired", expiredCount);

        // Phase 2: warn about entries approaching expiry (still pending, not yet past deadline).
        if (transport is not null && options.DocketExpiryWarningWindow > TimeSpan.Zero)
        {
            var warningThreshold = now.Add(options.DocketExpiryWarningWindow);
            await ForEachPendingAsync(store, sweepScope, batchSize, ct, async entry =>
            {
                if (entry.ExpiresAt > warningThreshold) return;
                await transport.BroadcastToGroupAsync(
                    entry.SessionId, TransportEvent.DocketExpiring,
                    new DocketExpiringNotification(entry.EntryId, entry.ExpiresAt), ct);
            });
        }

        // Phase 3: re-broadcast the Evidence Card for every entry still pending after phase 1.
        if (transport is not null)
        {
            await ForEachPendingAsync(store, sweepScope, batchSize, ct, async entry =>
            {
                var request = await EvidenceCardRequestFactory.CreateAsync(
                    store, entry.EntryId, entry.Envelope, entry.ExpiresAt, ct);
                await transport.BroadcastToGroupAsync(
                    entry.SessionId, TransportEvent.EvidenceCardRequest, request, ct);
            });
        }
    }

    /// <summary>
    /// Walks every entry that currently reads pending, one bounded page at a time.
    /// </summary>
    /// <remarks>
    /// The sweep is the one caller with a legitimate reason to read across tenants — it is the host's
    /// own scheduled maintenance, not a caller acting on somebody's behalf — and even it reads in
    /// pages, because "every pending entry in the deployment" is precisely the read that is fine in
    /// development and fatal in production.
    /// </remarks>
    private static async Task ForEachPendingAsync(
        IDocketStore store, DocketScope scope, int pageSize, CancellationToken ct, Func<DocketEntry, Task> act)
    {
        string? cursor = null;
        do
        {
            var page = await store.ListPendingAsync(scope, new DocketPage(pageSize, cursor), ct);
            foreach (var entry in page.Items) await act(entry);
            cursor = page.Cursor;
        }
        while (cursor is not null);
    }
}
