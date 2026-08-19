namespace Affiant.Policies.Services;

/// <summary>
/// Default <see cref="RiskScoreCalculatorBase"/> provided by the framework.
/// Hosts can replace via <c>SetRiskScoreCalculator&lt;T&gt;()</c> in <c>AddAffiantPolicies()</c>.
/// </summary>
public sealed class DefaultRiskScoreCalculator : RiskScoreCalculatorBase
{
    // Inherits the default ComputeAsync implementation from RiskScoreCalculatorBase.
}
