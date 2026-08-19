namespace Affiant.Abstractions.Models;

/// <summary>
/// Captures the context needed to file and route a review through the state machine.
/// Passed to <see cref="Interfaces.IApprovalPolicy.EvaluateAsync"/> and <c>ReviewGate.FileReviewAsync</c>.
/// </summary>
public record ReviewContext(
    string SessionId,
    string TenantId,
    string UserId,
    string ReviewerUserId,
    Affidavit Affidavit,
    Guid? EntryId = null,
    IReadOnlyDictionary<string, object?>? Amendments = null);
