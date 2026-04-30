namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

public sealed class ApprovalPolicyEvaluator(IEnumerable<IApprovalPolicy> policies) : IApprovalPolicyEvaluator
{
    public async Task<ReviewRequirement> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
    {
        foreach (var policy in policies)
        {
            var requirement = await policy.EvaluateAsync(affidavit, cancellationToken).ConfigureAwait(false);
            if (requirement is not null)
                return requirement.Value;
        }
        return ReviewRequirement.ReviewerConfirmation;
    }
}
