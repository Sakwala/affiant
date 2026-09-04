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
/// without one fails on its first evaluation, before any write is auto-approved, rather than
/// approving or refusing on an unscored guess.
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

        // Deliberately no risk-configuration check here. RiskThreshold is virtual, so reading it
        // from this constructor dispatches to an override whose own fields are not yet assigned —
        // an override that reads injected configuration (`=> _config.Ceiling`) would throw a
        // NullReferenceException during DI resolution. The check lives at the top of
        // EvaluateAsync instead, where the object is fully built, and is available eagerly to a
        // host that wants it via AffiantPolicies.ValidateStandingOrders(IServiceProvider).
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

    /// <summary>
    /// Throws when this Standing Order declares a <see cref="RiskThreshold"/> but has no usable
    /// <see cref="RiskScoreCalculatorBase"/> — none injected, or only the placeholder
    /// <c>AddAffiantPolicies</c> registers when the host supplied no calculator.
    /// Returns the declared ceiling and the calculator that will score it; both null when the
    /// order declares no ceiling and so needs no calculator at all.
    /// </summary>
    internal (int? Threshold, RiskScoreCalculatorBase? Scorer) EnsureConfigured()
    {
        var threshold = RiskThreshold;
        if (threshold is null)
            return (null, null);

        var scorer = RiskScorer;
        if (scorer is null or MissingRiskScoreCalculator)
            throw new InvalidOperationException(MissingRiskScoreCalculator.MessageFor(GetType()));

        return (threshold, scorer);
    }

    public async Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
    {
        // Configuration first, before the conditions are even tested: a Standing Order that
        // declares a risk ceiling with no calculator to score it fails here — on its first
        // evaluation, before any write is auto-approved, never silently.
        var (threshold, scorer) = EnsureConfigured();

        if (!await MatchesAsync(affidavit, cancellationToken).ConfigureAwait(false))
            return null;

        // EnsureConfigured returns a scorer whenever it returns a threshold, and throws otherwise,
        // so a null scorer here can only mean this order declares no ceiling.
        if (threshold is null || scorer is null)
        {
            var matchApproverId = await GetAutoApproverIdAsync(affidavit, cancellationToken).ConfigureAwait(false);
            Logger.LogInformation(
                "Standing Order {Policy} auto-approved: conditions matched, no risk ceiling declared, approver {Approver}",
                GetType().Name, matchApproverId ?? "[system]");
            return ReviewRequirement.StandingOrder;
        }

        var riskScore = await scorer.ComputeAsync(affidavit, cancellationToken).ConfigureAwait(false);

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
}
