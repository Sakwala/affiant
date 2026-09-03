using Affiant.Abstractions.Models;

namespace Affiant.Policies.Services;

/// <summary>
/// Computes risk scores for Affidavits.
/// The framework ships no scoring formula: what counts as risky is a property of the host's
/// domain, not of the evidence layer. Hosts subclass, implement <see cref="ComputeAsync"/>,
/// and register the subclass with <c>SetRiskScoreCalculator&lt;T&gt;()</c> inside
/// <c>AddAffiantPolicies(...)</c>. A calculator is needed only when a Standing Order declares
/// a <c>RiskThreshold</c>.
/// </summary>
public abstract class RiskScoreCalculatorBase
{
    /// <summary>
    /// Computes a numeric risk score for the given Affidavit, on the <see cref="RiskLevel"/>
    /// scale: 1 = low, 2 = medium, 3 = high. A Standing Order auto-approves when this score is
    /// at or below the ceiling it declares, so a formula that never returns the lowest band
    /// makes every order that declares that band unreachable.
    /// </summary>
    public abstract Task<int> ComputeAsync(Affidavit affidavit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies a numeric score into a <see cref="RiskLevel"/>, clamping out-of-range values.
    /// </summary>
    public RiskLevel ClassifyScore(int score) => (RiskLevel)Math.Clamp(score, 1, 3);
}
