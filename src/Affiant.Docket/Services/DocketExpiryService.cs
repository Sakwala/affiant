using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Affiant.Docket.Services;

/// <summary>
/// Background sweep that transitions overdue Pending <c>DocketEntry</c> rows to Expired and, when
/// an <see cref="IStreamingTransport"/> is available, warns the UI as entries approach expiry,
/// notifies it once they are marked expired (framework half of repo issue #10 / triage F0-A6), and
/// unconditionally re-broadcasts every still-Pending entry's Evidence Card (Area-5 Decision 3,
/// affiant#28 — at-least-once delivery by construction).
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
    /// Three independent phases run each tick:
    /// <list type="number">
    /// <item>Entries already past <c>ExpiresAt</c> are each CAS-transitioned to Expired one at a
    /// time (not a single bulk statement — see method body); if a transport is registered, a
    /// <see cref="TransportEvent.DocketExpired"/> is broadcast per entry this tick's own write
    /// actually transitioned, never for one a concurrent decision already claimed.</item>
    /// <item>Entries still Pending but within <see cref="AffiantCoreOptions.DocketExpiryWarningWindow"/>
    /// of <c>ExpiresAt</c> get a <see cref="TransportEvent.DocketExpiring"/> broadcast. This set is
    /// re-queried every tick, so a warning is re-emitted on every tick the entry remains inside the
    /// window — clients must treat repeated warnings for the same docket id as idempotent (e.g. key
    /// a UI countdown off the notification's <c>ExpiresAt</c> rather than counting notifications).</item>
    /// <item>
    /// Every entry still <see cref="ReviewStatus.Pending"/> after phase 1 gets its
    /// <see cref="TransportEvent.EvidenceCardRequest"/> re-broadcast — unconditionally, regardless of
    /// whether the entry's filing-time broadcast reported success (Area-5 Decision 3, affiant#28).
    /// This is the framework's chosen closure for the stranded-entry window a
    /// <c>CardDelivered</c>-style flag can't honestly close (a SignalR group send to zero connected
    /// members completes successfully): at-least-once by construction, applying the same
    /// idempotent-repeat contract phase 2 already established for <c>DocketExpiring</c> to the card
    /// itself, via the same builder <see cref="Affiant.Core.Services.ReviewGate"/>'s filing path and
    /// <see cref="Affiant.Core.Services.ReviewGate.RebroadcastPendingCardsAsync"/> use, so all three
    /// payloads for a given entry cannot drift. This closes redelivery-until-acted; it does not
    /// prove a human saw the card.
    /// </item>
    /// </list>
    /// </remarks>
    public async Task ExpireOverdueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocketStore>();

        var now = DateTimeOffset.UtcNow;

        // Phase 1: expire entries already past their deadline, one CAS-guarded write at a time —
        // not a single bulk MarkExpiredAsync statement — so this tick can tell, per entry, whether
        // ITS OWN write is the one that actually transitioned Pending -> Expired. A concurrent
        // decision (ReviewGate.HandleDecisionAsync's restart path, affiant#14) can independently
        // win that same transition for an entry already in this tick's ListExpiredAsync snapshot;
        // DocketExpiryBroadcaster may only be invoked by whichever caller's write affected the row
        // (see its remarks) — re-verifying status after a bulk statement that reports no per-row
        // outcome cannot tell the two apart and double-broadcasts DocketExpired.
        var expired = await store.ListExpiredAsync(now, ct);
        if (expired.Count > 0)
        {
            var wonCount = 0;
            foreach (var entry in expired)
            {
                var rowsAffected = await store.UpdateReviewStatusAsync(
                    entry.EntryId, ReviewStatus.Expired, ct);
                if (rowsAffected == 0)
                    continue; // lost the race to a concurrent transition — that caller owns the broadcast

                wonCount++;
                if (transport is not null)
                {
                    await DocketExpiryBroadcaster.VerifyAndBroadcastIfExpiredAsync(
                        store, transport, entry.EntryId, ct);
                }
            }

            if (wonCount > 0)
                logger.LogInformation("Marked {Count} docket entries as expired", wonCount);
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

        // Phase 3: unconditionally re-broadcast EvidenceCardRequest for every entry still Pending
        // after phase 1 — Area-5 Decision 3, affiant#28. Re-queried every tick (phase 1 may have
        // just expired some of last tick's candidates), so this is the redelivery mechanism, not a
        // one-shot retry.
        if (transport is not null)
        {
            var stillPending = await store.ListAllPendingAsync(ct);
            foreach (var entry in stillPending)
            {
                var request = await EvidenceCardRequestFactory.CreateAsync(
                    store, entry.EntryId, entry.Envelope, entry.ExpiresAt, ct);
                await transport.BroadcastToGroupAsync(
                    entry.SessionId, TransportEvent.EvidenceCardRequest, request, ct);
            }
        }
    }
}
