using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
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

            // TL-1 `standing-order.fired` (AZ-1): a write was approved with no person present, which
            // is the single most consequential thing a policy can do and the one an operator most
            // needs to be able to count. `entry.id` is absent because IApprovalPolicy.EvaluateAsync
            // is handed the Affidavit alone — the gate has not filed an entry when the chain runs.
            AffiantTelemetry.RecordStandingOrderFired(PolicyId, riskScore, policyVersion: PolicyVersion);

            return ReviewRequirement.StandingOrder;
        }

        Logger.LogInformation(
            "Standing Order {Policy} matched conditions but risk {Score} exceeds threshold {Threshold}",
            GetType().Name, riskScore, RiskThreshold);

        // TL-1 `standing-order.blocked` (GT-5). `risk-above-threshold` is the only blocked reason
        // this release can raise: the mandatory-Empty check (GT-5) and the declared-input binding
        // check (PV-4) land with the gate-pipeline change, and both emit this same key with their
        // own `blocked.reason` code, which is why the code is a separate attribute from the sentence.
        AffiantTelemetry.RecordStandingOrderBlocked(
            PolicyId,
            blockedReason: "risk-above-threshold",
            reason:
                $"The Standing Order matched, but the operation's risk score ({riskScore}) is above " +
                $"the policy's threshold ({RiskThreshold}), so a person still confirms this write.",
            riskScore: riskScore,
            riskThreshold: RiskThreshold,
            policyVersion: PolicyVersion);

        return null;
    }

    /// <summary>
    /// The policy's identity in telemetry (<c>policy.id</c>). Defaults to the concrete policy type's
    /// full name, which is stable across releases in a way a display name is not. Override it when
    /// a host names its policies in configuration and wants alerts keyed on that name instead.
    /// </summary>
    protected virtual string PolicyId => GetType().FullName ?? GetType().Name;

    /// <summary>
    /// The policy's own version in telemetry (<c>policy.version</c>), or <see langword="null"/> when
    /// the policy does not version itself. Override it when a host revises a policy's rules and
    /// needs to tell an approval made under the old rules from one made under the new.
    /// </summary>
    protected virtual string? PolicyVersion => null;
}
