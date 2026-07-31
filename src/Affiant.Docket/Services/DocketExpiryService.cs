using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Affiant.Docket.Services;

/// <summary>
/// Background sweep that transitions overdue Pending <c>DocketEntry</c> rows to Expired and, when
/// an <see cref="IStreamingTransport"/> is available, warns the UI as entries approach expiry and
/// notifies it once they are marked expired (framework half of repo issue #10 / triage F0-A6).
/// </summary>
/// <remarks>
/// <paramref name="transport"/> is optional — the Affiant.Docket package must not hard-require a
/// transport dependency. Hosts that register an <see cref="IStreamingTransport"/> (e.g. via
/// Affiant.Transport.SignalR) get expiry notifications for free; hosts that don't simply skip the
/// broadcast half of each tick, unchanged from prior behavior.
/// </remarks>
public sealed class DocketExpiryService(
    IServiceScopeFactory scopeFactory,
    AffiantCoreOptions options,
    ILogger<DocketExpiryService> logger,
    IStreamingTransport? transport = null) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

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
    /// Runs one expiry tick. Public for testability — integration tests call this directly
    /// instead of waiting for the background timer.
    /// </summary>
    /// <remarks>
    /// Two independent phases run each tick:
    /// <list type="number">
    /// <item>Entries already past <c>ExpiresAt</c> are bulk-marked Expired; if a transport is
    /// registered, a <see cref="TransportEvent.DocketExpired"/> is broadcast per entry.</item>
    /// <item>Entries still Pending but within <see cref="AffiantCoreOptions.DocketExpiryWarningWindow"/>
    /// of <c>ExpiresAt</c> get a <see cref="TransportEvent.DocketExpiring"/> broadcast. This set is
    /// re-queried every tick, so a warning is re-emitted on every tick the entry remains inside the
    /// window — clients must treat repeated warnings for the same docket id as idempotent (e.g. key
    /// a UI countdown off the notification's <c>ExpiresAt</c> rather than counting notifications).</item>
    /// </list>
    /// </remarks>
    public async Task ExpireOverdueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocketStore>();

        var now = DateTimeOffset.UtcNow;

        // Phase 1: bulk-expire entries already past their deadline.
        var expired = await store.ListExpiredAsync(now, ct);
        if (expired.Count > 0)
        {
            var expiredIds = expired.Select(e => e.EntryId).ToList();
            await store.MarkExpiredAsync(expiredIds, ct);
            logger.LogInformation("Marked {Count} docket entries as expired", expiredIds.Count);

            if (transport is not null)
            {
                foreach (var entry in expired)
                {
                    // MarkExpiredAsync applies the same WHERE Status = 'Pending' guard as
                    // UpdateReviewStatusAsync but reports no affected count, so a candidate
                    // collected in the ListExpiredAsync snapshot above may have already been
                    // approved/rejected by the time the bulk update ran. Re-read the entry and
                    // broadcast only if it is genuinely Expired now — otherwise we'd tell the
                    // session group an entry expired when it actually didn't.
                    var current = await store.GetDocketEntryAsync(entry.EntryId, ct);
                    if (current?.Status != ReviewStatus.Expired)
                        continue;

                    await transport.BroadcastToGroupAsync(
                        current.SessionId, TransportEvent.DocketExpired,
                        new DocketExpiredNotification(current.EntryId), ct);
                }
            }
        }

        // Phase 2: warn about entries approaching expiry (still Pending, not yet past deadline).
        if (transport is not null && options.DocketExpiryWarningWindow > TimeSpan.Zero)
        {
            var warningThreshold = now.Add(options.DocketExpiryWarningWindow);
            var withinWarningWindow = await store.ListExpiredAsync(warningThreshold, ct);

            foreach (var entry in withinWarningWindow)
            {
                if (entry.ExpiresAt <= now)
                    continue; // already handled (and expired) in phase 1

                await transport.BroadcastToGroupAsync(
                    entry.SessionId, TransportEvent.DocketExpiring,
                    new DocketExpiringNotification(entry.EntryId, entry.ExpiresAt), ct);
            }
        }
    }
}
