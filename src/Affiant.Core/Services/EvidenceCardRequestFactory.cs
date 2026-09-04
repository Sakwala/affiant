namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;

/// <summary>
/// Builds the <see cref="EvidenceCardRequest"/> payload for a <see cref="DocketEntry"/> — the one
/// place <see cref="ReviewGate"/>'s filing path, its <see cref="ReviewGate.RebroadcastPendingCardsAsync"/>
/// reconnect primitive, and <c>DocketExpiryService</c>'s sweep all go through, so the payload for a
/// given entry cannot drift between the three (Area-5 Decision 3, affiant#28).
///
/// <para>
/// <b>A blocked entry says so on its card</b> (AZ-4, CV-4), and never claims a confirmation is being
/// awaited: the fact lives on the Docket row — the row is what is blocked — and the caller filing a
/// blocked entry passes the marker through here so the card reports the row's own value rather than
/// a second one computed for display.
/// </para>
/// </summary>
public static class EvidenceCardRequestFactory
{
    /// <summary>
    /// Re-derives <see cref="EvidenceCardRequest.PriorAmendments"/> via
    /// <see cref="IDocketStore.GetResubmissionParentAsync"/> when <paramref name="entryId"/> is
    /// itself the product of a resubmission (Area-5 Decision 2, affiant#31) — the same reverse
    /// lookup <c>SessionRehydrator.RehydrateAsync</c> uses, so this is its only other caller.
    /// </summary>
    /// <param name="docketStore">The store the resubmission parent is looked up in.</param>
    /// <param name="entryId">The Docket entry this card is filed under.</param>
    /// <param name="affidavit">The record awaiting a decision.</param>
    /// <param name="expiresAt">When the review window closes (GT-4).</param>
    /// <param name="hostOperation">
    /// The host's own verb for the operation, carried beside the protocol's shape vocabulary so a
    /// reviewer surface can head the card with the term a person recognises. Null when the host
    /// named none.
    /// </param>
    /// <param name="ct">Cancels the store lookup.</param>
    /// <param name="blocked">
    /// Why no decision on this entry will be accepted, or <c>null</c> when it can be decided
    /// (AZ-4). A blocked card never asks for a confirmation no decision path would accept.
    /// </param>
    /// <param name="requiresConfirmation">
    /// Overrides whether the card asks a person. <c>false</c> for a Standing Order approval: the
    /// write is already approved and the card is there so the reviewer surface can see what was
    /// approved in the organisation's name, not so anyone can confirm it (SR-4). <c>null</c> leaves
    /// the record's own answer.
    /// </param>
    public static async Task<EvidenceCardRequest> CreateAsync(
        IDocketStore docketStore,
        Guid entryId,
        Affidavit affidavit,
        DateTimeOffset expiresAt,
        CancellationToken ct,
        string? hostOperation = null,
        BlockedMarker? blocked = null,
        bool? requiresConfirmation = null)
    {
        ArgumentNullException.ThrowIfNull(docketStore);

        var parent = await docketStore.GetResubmissionParentAsync(entryId, ct);
        var priorAmendments = parent?.Amendments is { Count: > 0 } ? parent.Amendments : null;

        // Everything the envelope repeats — the two companion confidence numbers (AF-2), the
        // warnings, whether a person must confirm, the per-field presentation hints — is lifted
        // from the record itself rather than passed in, so a card can never report a number that
        // was about a different set of values than the ones it shows.
        var card = EvidenceCardRequest.For(
            entryId,
            affidavit,
            expiresAt,
            priorAmendments,
            blocked,
            hostOperation);

        return requiresConfirmation is { } asked && card.RequiresConfirmation != asked
            ? card with { RequiresConfirmation = asked }
            : card;
    }
}
