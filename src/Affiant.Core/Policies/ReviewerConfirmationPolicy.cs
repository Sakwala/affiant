namespace Affiant.Core.Policies;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

public sealed class ReviewerConfirmationPolicy : IApprovalPolicy
{
    public Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
        => Task.FromResult<ReviewRequirement?>(ReviewRequirement.ReviewerConfirmation);
}
