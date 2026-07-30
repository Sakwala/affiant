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
public record EvidenceCardRequest(
    Guid DocketId,
    Affidavit Affidavit,
    DateTimeOffset RequiredBy);

/// <summary>
/// Payload returned by the UI after the reviewer acts on an Evidence Card.
/// Transported via <see cref="TransportEvent.EvidenceCardResponse"/>.
///
/// <see cref="Amendments"/> carries the fields the reviewer edited before approving —
/// keyed by <see cref="AffidavitField.Name"/>, values are the reviewer's replacement
/// value (<c>null</c> means the reviewer explicitly cleared the field). Null or empty on
/// rejection, or when the reviewer approved without editing anything. The framework's review
/// gate service persists these onto the <see cref="DocketEntry"/> it owns; appending
/// <see cref="ProvenanceTag"/> UserStated tags to the amended fields' provenance chains
/// is the host's <see cref="Interfaces.IWriteExecutor"/> overlay's responsibility — see
/// framework spec §6 Rule 7 and §2.7.
/// </summary>
public record EvidenceCardResponse(
    Guid DocketId,
    ApprovalDecision Decision,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Amendments = null);
