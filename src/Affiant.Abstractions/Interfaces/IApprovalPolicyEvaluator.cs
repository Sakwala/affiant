namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IApprovalPolicyEvaluator
{
    /// <summary>
    /// Evaluates registered <see cref="IApprovalPolicy"/> implementations in declared order
    /// and returns the first non-null <see cref="ReviewRequirement"/>.
    /// Falls back to <see cref="ReviewRequirement.ReviewerConfirmation"/> if no policy matches.
    /// </summary>
    Task<ReviewRequirement> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default);
}
