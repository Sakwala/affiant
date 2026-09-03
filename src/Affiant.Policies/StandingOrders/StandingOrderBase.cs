using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Policies.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Affiant.Policies.StandingOrders;

/// <summary>
/// Base class for Standing Order policies that auto-approve operations without human review.
/// A Standing Order auto-approves when the Affidavit matches the policy's conditions and,
/// if the policy declares a <see cref="RiskThreshold"/>, the host-computed risk score is at or
/// below it.
///
/// Subclass and implement <see cref="MatchesAsync"/> to describe when the order applies — that
/// alone is a complete, working Standing Order. Override <see cref="RiskThreshold"/> only to
/// add a risk ceiling on top of the match, which requires a <see cref="RiskScoreCalculatorBase"/>
/// registered with <c>SetRiskScoreCalculator&lt;T&gt;()</c>; a policy that declares a ceiling
/// without one throws rather than approving or refusing on an unscored guess.
/// Override <see cref="GetAutoApproverIdAsync"/> to record a named approver in logs.
/// </summary>
public abstract class StandingOrderBase : IApprovalPolicy
{
    protected readonly ILogger Logger;

    /// <summary>
    /// The host's risk calculator, or null when this Standing Order declares no
    /// <see cref="RiskThreshold"/> and therefore needs no score.
    /// </summary>
    protected readonly RiskScoreCalculatorBase? RiskScorer;

    /// <summary>
    /// Risk score at or below which the Standing Order auto-approves, or null — the default —
    /// for no risk ceiling at all: matching the conditions is the whole test.
    /// </summary>
    protected virtual int? RiskThreshold => null;

    protected StandingOrderBase(RiskScoreCalculatorBase? riskScorer = null, ILogger? logger = null)
    {
        RiskScorer = riskScorer;
        Logger = logger ?? NullLogger.Instance;

        // Read here so a host that declares a ceiling but registers no calculator fails when the
        // container builds the policy rather than on the first write it was meant to gate. The
        // same check runs again in EvaluateAsync, which covers a subclass whose threshold depends
        // on state its own constructor assigns after this one returns.
        if (RiskThreshold is not null && riskScorer is null)
            throw new InvalidOperationException(MissingRiskScorerMessage(GetType()));
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

        var threshold = RiskThreshold;

        if (threshold is null)
        {
            var matchApproverId = await GetAutoApproverIdAsync(affidavit, cancellationToken).ConfigureAwait(false);
            Logger.LogInformation(
                "Standing Order {Policy} auto-approved: conditions matched, no risk ceiling declared, approver {Approver}",
                GetType().Name, matchApproverId ?? "[system]");
            return ReviewRequirement.StandingOrder;
        }

        if (RiskScorer is null)
            throw new InvalidOperationException(MissingRiskScorerMessage(GetType()));

        var riskScore = await RiskScorer.ComputeAsync(affidavit, cancellationToken).ConfigureAwait(false);

        if (riskScore <= threshold.Value)
        {
            var approverId = await GetAutoApproverIdAsync(affidavit, cancellationToken).ConfigureAwait(false);
            Logger.LogInformation(
                "Standing Order {Policy} auto-approved: risk {Score} ≤ threshold {Threshold}, approver {Approver}",
                GetType().Name, riskScore, threshold.Value, approverId ?? "[system]");
            return ReviewRequirement.StandingOrder;
        }

        Logger.LogInformation(
            "Standing Order {Policy} matched conditions but risk {Score} exceeds threshold {Threshold}",
            GetType().Name, riskScore, threshold.Value);
        return null;
    }

    private static string MissingRiskScorerMessage(Type policyType) =>
        $"Standing Order '{policyType.Name}' declares a RiskThreshold but no RiskScoreCalculatorBase " +
        "is registered. Register one with SetRiskScoreCalculator<T>() inside AddAffiantPolicies(...), " +
        "or remove the RiskThreshold override so the order auto-approves on its conditions alone.";
}
