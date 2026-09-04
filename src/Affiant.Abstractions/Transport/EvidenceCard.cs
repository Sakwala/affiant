namespace Affiant.Abstractions.Transport;

using Affiant.Abstractions.Models;

/// <summary>Reviewer's decision on an EvidenceCardRequest.</summary>
public enum ApprovalDecision
{
    Approved,
    Rejected
}

/// <summary>
/// Payload sent to the UI when a WriteProposal enters the review queue.
/// Transported via <see cref="TransportEvent.EvidenceCardRequest"/>.
/// </summary>
/// <remarks>
/// <b>At-least-once delivery, no receipt guarantee (Area-5 Decision 3, affiant#28).</b>
/// <see cref="Interfaces.IStreamingTransport.BroadcastToGroupAsync"/> only reports whether the
/// underlying send call completed, never whether a human received or rendered the card — a session
/// group with zero currently-connected members completes the broadcast successfully with zero
/// recipients. The framework compensates by re-broadcasting this event for every entry still
/// <see cref="ReviewStatus.Pending"/> on every 30-second <c>DocketExpiryService</c> sweep
/// tick, and again on session reconnect via <c>ReviewGate.RebroadcastPendingCardsAsync</c>, until the
/// entry is acted on or expires — the same idempotent-repeat contract
/// <see cref="DocketExpiringNotification"/> already documents for <c>DocketExpiring</c>, applied here
/// to the card itself. Clients MUST treat a repeated <c>EvidenceCardRequest</c> for the same
/// <see cref="DocketId"/> as idempotent — render or update the existing card in place, never append a
/// duplicate. This closes "the client gets the card again on reconnect/next sweep tick until it acts
/// or the entry expires"; it does NOT prove a human ever saw the card — that stronger guarantee would
/// need a separate, costed client-ack RPC and is explicitly out of scope here (Area-5 D3 research
/// pack, §1/§5 criterion 7).
/// </remarks>
/// <param name="PriorAmendments">
/// Set only when this Evidence Card is a resubmission of a previously expired review (framework
/// half of repo issue #9) — carries the amendments a reviewer made on the original, expired entry
/// before the window lapsed, so the new reviewer can see what was already agreed. <c>null</c> for
/// a first-time filing.
/// </param>
public record EvidenceCardRequest(
    Guid DocketId,
    Affidavit Affidavit,
    DateTimeOffset RequiredBy,
    IReadOnlyDictionary<string, object?>? PriorAmendments = null);

/// <summary>
/// Payload returned by the UI after the reviewer acts on an Evidence Card.
/// Transported via <see cref="TransportEvent.EvidenceCardResponse"/>.
///
/// <see cref="Amendments"/> carries the fields the reviewer edited before approving —
/// keyed by <see cref="AffidavitField.Name"/>, values are the reviewer's replacement
/// value (<c>null</c> means the reviewer explicitly cleared the field). Null or empty on
/// rejection, or when the reviewer approved without editing anything. The framework's review
/// gate service persists these onto the <see cref="DocketEntry"/> it owns <em>and</em> folds them
/// into an amended <see cref="Affidavit"/> beside the proposal — the reviewer's act on each amended
/// field's chain and the three confidence numbers recomputed — returned on
/// <see cref="ReviewOutcome.Approved.AmendedAffidavit"/>. See
/// <see cref="AffidavitAmendments.Apply"/>, which is what a host's
/// <see cref="Interfaces.IWriteExecutor"/> should use rather than stamping tags by hand.
/// </summary>
public record EvidenceCardResponse(
    Guid DocketId,
    ApprovalDecision Decision,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Amendments = null);
