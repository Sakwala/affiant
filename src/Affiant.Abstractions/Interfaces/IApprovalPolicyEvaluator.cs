namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IApprovalPolicyEvaluator
{
    /// <summary>
    /// Evaluates registered <see cref="IApprovalPolicy"/> implementations in declared order and
    /// returns the first non-null <see cref="ApprovalVerdict"/>, with the GT-5 and PV-4 checks
    /// applied to it and its review window resolved against the policy's own declared default.
    /// Falls back to <see cref="ReviewRequirement.ReviewerConfirmation"/> if no policy matches — the
    /// safe default is always a human.
    /// </summary>
    /// <exception cref="Exceptions.AffiantPolicyException">
    /// A policy named a review window that is not a deadline, or its <c>EvaluateAsync</c> threw
    /// (CV-1). Nothing is filed.
    /// </exception>
    /// <param name="affidavit">The proposed write, as sworn.</param>
    /// <param name="identity">
    /// Where the proposal came from, passed through to every policy in the chain so it can bind.
    /// Never used to authorize an actor — see <see cref="IApprovalPolicy"/>'s remarks.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    Task<ApprovalVerdict> EvaluateAsync(
        Affidavit affidavit,
        ConversationIdentity identity,
        CancellationToken cancellationToken = default);
}
