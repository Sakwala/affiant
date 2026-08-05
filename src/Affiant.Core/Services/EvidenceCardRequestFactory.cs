namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;

/// <summary>
/// Builds the <see cref="EvidenceCardRequest"/> payload for a <see cref="DocketEntry"/> — the one
/// place <see cref="ReviewGate"/>'s filing path, its <see cref="ReviewGate.RebroadcastPendingCardsAsync"/>
/// reconnect primitive, and <c>DocketExpiryService</c>'s sweep all go through, so the payload for a
/// given entry cannot drift between the three (Area-5 Decision 3, affiant#28).
/// </summary>
public static class EvidenceCardRequestFactory
{
    /// <summary>
    /// Re-derives <see cref="EvidenceCardRequest.PriorAmendments"/> via
    /// <see cref="IDocketStore.GetResubmissionParentAsync"/> when <paramref name="entryId"/> is
    /// itself the product of a resubmission (Area-5 Decision 2, affiant#31) — the same reverse
    /// lookup <c>SessionRehydrator.RehydrateAsync</c> uses, so this is its only other caller.
    /// </summary>
    public static async Task<EvidenceCardRequest> CreateAsync(
        IDocketStore docketStore,
        Guid entryId,
        Affidavit affidavit,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var parent = await docketStore.GetResubmissionParentAsync(entryId, ct);
        var priorAmendments = parent?.Amendments is { Count: > 0 } ? parent.Amendments : null;
        return new EvidenceCardRequest(entryId, affidavit, expiresAt, priorAmendments);
    }
}
