namespace Affiant.Abstractions.Models;

/// <summary>
/// Captures the context needed to file and route a review through the state machine.
/// Passed to <see cref="Interfaces.IApprovalPolicy.EvaluateAsync"/> and <c>ReviewGate.FileReviewAsync</c>.
/// </summary>
/// <param name="SessionId">The conversation the proposal came from.</param>
/// <param name="TenantId">The tenant the entry is scoped to.</param>
/// <param name="UserId">The user whose turn produced the proposal.</param>
/// <param name="ReviewerUserId">Who is being asked to decide.</param>
/// <param name="Affidavit">The sworn evidence record, as proposed.</param>
/// <param name="EntryId">The entry id to file under, when the caller has already minted one.</param>
/// <param name="Amendments">Amendments known at filing time.</param>
/// <param name="Supersedes">
/// The expired entry this filing resubmits, or <c>null</c> for a first filing. Written onto the new
/// row's lineage; the successor half is written on the superseded row, so the history reads forward
/// from either end.
/// </param>
public record ReviewContext(
    string SessionId,
    string TenantId,
    string UserId,
    string ReviewerUserId,
    Affidavit Affidavit,
    Guid? EntryId = null,
    IReadOnlyDictionary<string, object?>? Amendments = null,
    Guid? Supersedes = null);
