namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;

/// <summary>
/// Shared "never lie to the session group" gate for every Pending → Expired write path: re-reads
/// the entry after its own guarded transition and broadcasts <see cref="TransportEvent.DocketExpired"/>
/// only if the entry is genuinely <see cref="ReviewStatus.Expired"/> at that moment — a concurrent
/// decision may have already won the row instead. Used by <c>DocketExpiryService</c>'s sweep and
/// <c>ReviewGate.HandleDecisionAsync</c>'s restart path (affiant#14) so this idiom cannot
/// drift between the two.
/// </summary>
public static class DocketExpiryBroadcaster
{
    /// <summary>
    /// Re-reads <paramref name="entryId"/> and broadcasts <see cref="TransportEvent.DocketExpired"/>
    /// iff its current status is <see cref="ReviewStatus.Expired"/>.
    /// </summary>
    /// <remarks>
    /// Callers whose own guarded write did NOT affect a row (lost the CAS, or replaying a decision
    /// against an already-terminal entry) must not call this — doing so would re-broadcast for
    /// every repeat call as long as the entry stays Expired. Only the call whose own transition
    /// attempt won should invoke this.
    /// </remarks>
    /// <returns>The entry's genuine current status, or <c>null</c> if it no longer exists.</returns>
    public static async Task<ReviewStatus?> VerifyAndBroadcastIfExpiredAsync(
        IDocketStore docketStore,
        IStreamingTransport transport,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        var current = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
        if (current?.Status != ReviewStatus.Expired)
            return current?.Status;

        await transport.BroadcastToGroupAsync(
            current.SessionId, TransportEvent.DocketExpired,
            new DocketExpiredNotification(entryId), cancellationToken);
        return ReviewStatus.Expired;
    }
}
