namespace Affiant.Core.Policies;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// The policy that always asks a person, and names no review window of its own — the gate's default
/// then applies (protocol rule GT-4). Registered by hosts that want the safe answer stated
/// explicitly rather than inherited from the chain's fall-through.
/// </summary>
public sealed class ReviewerConfirmationPolicy : IApprovalPolicy
{
    public Task<ApprovalVerdict?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
        => Task.FromResult<ApprovalVerdict?>(new ApprovalVerdict(ReviewRequirement.ReviewerConfirmation));
}
