using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Policies.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Affiant.Policies.StandingOrders;

/// <summary>
/// Base class for Standing Order policies that auto-approve low-risk operations.
/// A Standing Order auto-approves when the Affidavit matches the policy's conditions
/// AND the computed risk score is within <see cref="RiskThreshold"/>.
///
/// Subclass and implement <see cref="MatchesAsync"/> to describe when the order applies.
/// Override <see cref="RiskThreshold"/> to raise the auto-approval ceiling.
/// Override <see cref="GetAutoApproverIdAsync"/> to record a named approver in logs.
/// </summary>
public abstract class StandingOrderBase : IApprovalPolicy
{
    protected readonly ILogger Logger;
    protected readonly RiskScoreCalculatorBase RiskScorer;

    /// <summary>
    /// Risk score at or below which the Standing Order auto-approves.
    /// Default: <see cref="RiskLevel.Low"/> (score = 1).
    /// </summary>
    protected virtual int RiskThreshold => (int)RiskLevel.Low;

    protected StandingOrderBase(RiskScoreCalculatorBase riskScorer, ILogger? logger = null)
    {
        RiskScorer = riskScorer ?? throw new ArgumentNullException(nameof(riskScorer));
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Returns true if this Standing Order's conditions match the given Affidavit.
    /// </summary>
    protected abstract Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the ID of the user recorded as auto-approver, or null for system approval.
    /// Default: null.
    /// </summary>
    protected virtual Task<string?> GetAutoApproverIdAsync(Affidavit affidavit, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public async Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
    {
        if (!await MatchesAsync(affidavit, cancellationToken).ConfigureAwait(false))
            return null;

        var riskScore = await RiskScorer.ComputeAsync(affidavit, cancellationToken).ConfigureAwait(false);

        if (riskScore <= RiskThreshold)
        {
            var approverId = await GetAutoApproverIdAsync(affidavit, cancellationToken).ConfigureAwait(false);
            Logger.LogInformation(
                "Standing Order {Policy} auto-approved: risk {Score} ≤ threshold {Threshold}, approver {Approver}",
                GetType().Name, riskScore, RiskThreshold, approverId ?? "[system]");
            return ReviewRequirement.StandingOrder;
        }

        Logger.LogInformation(
            "Standing Order {Policy} matched conditions but risk {Score} exceeds threshold {Threshold}",
            GetType().Name, riskScore, RiskThreshold);
        return null;
    }
}
