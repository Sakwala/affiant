namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;

public sealed class ApprovalPolicyEvaluator(IEnumerable<IApprovalPolicy> policies) : IApprovalPolicyEvaluator
{
    public async Task<ReviewRequirement> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
    {
        foreach (var policy in policies)
        {
            ReviewRequirement? requirement;
            try
            {
                requirement = await policy.EvaluateAsync(affidavit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // TL-1 `policy.invalid` (CV-1): a host's policy broke its own contract. The throw is
                // NOT swallowed — a chain that cannot answer must not fall through to a weaker
                // requirement, and a policy that throws is a host bug that has to surface. The event
                // exists so an operator can see WHICH policy is throwing without reading a stack
                // trace out of an aggregated log, and so a rising rate is alertable.
                AffiantTelemetry.RecordPolicyInvalid(
                    policy.GetType().FullName ?? policy.GetType().Name,
                    option: "evaluate",
                    reason: $"{ex.GetType().Name}: {ex.Message}");
                throw;
            }

            if (requirement is not null)
                return requirement.Value;
        }

        return ReviewRequirement.ReviewerConfirmation;
    }
}
