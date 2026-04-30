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
/// </summary>
public record EvidenceCardResponse(
    Guid DocketId,
    ApprovalDecision Decision,
    string? Reason = null);
