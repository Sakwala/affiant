using Affiant.Abstractions.Models;

namespace Affiant.Policies.Services;

/// <summary>
/// The placeholder <see cref="RiskScoreCalculatorBase"/> that <c>AddAffiantPolicies</c> registers
/// when the host supplies none. It carries no formula and no risk floor: every call throws,
/// naming the registration that is missing.
///
/// It exists so that a Standing Order whose constructor takes <see cref="RiskScoreCalculatorBase"/>
/// as a required dependency still resolves from the container. Without it such an order fails at
/// the container's own call-site resolution with "Unable to resolve service for type
/// 'RiskScoreCalculatorBase'", which says nothing about the fix; with it, the order resolves and
/// the actionable message arrives from the policy itself on its first evaluation.
/// </summary>
internal sealed class MissingRiskScoreCalculator : RiskScoreCalculatorBase
{
    /// <summary>
    /// The message a host sees when a Standing Order declares a risk ceiling but no calculator
    /// is registered to score it.
    /// </summary>
    internal static string MessageFor(Type policyType) =>
        $"Standing Order '{policyType.Name}' declares a RiskThreshold but no RiskScoreCalculatorBase " +
        "is registered. Register one with SetRiskScoreCalculator<T>() inside AddAffiantPolicies(...), " +
        "or remove the RiskThreshold override so the order auto-approves on its conditions alone.";

    /// <summary>
    /// The message a host sees when it resolves <see cref="RiskScoreCalculatorBase"/> itself and
    /// asks the placeholder to score something.
    /// </summary>
    internal static string Message =>
        "No RiskScoreCalculatorBase is registered: the framework ships no scoring formula of its " +
        "own. Register one with SetRiskScoreCalculator<T>() inside AddAffiantPolicies(...).";

    public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);
}
