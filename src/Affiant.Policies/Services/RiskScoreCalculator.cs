using Affiant.Abstractions.Models;

namespace Affiant.Policies.Services;

/// <summary>
/// Computes risk scores for Affidavits.
/// Hosts subclass and override <see cref="ComputeAsync"/> for domain-specific scoring logic.
/// </summary>
public abstract class RiskScoreCalculator
{
    /// <summary>
    /// Computes a numeric risk score (1 = low, 3 = high) for the given Affidavit.
    /// Default implementation checks for a "Value" field and scores by magnitude:
    /// &gt;$50 → High (3), present → Medium (2), absent → Medium (2).
    /// </summary>
    public virtual async Task<int> ComputeAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        var valueField = affidavit.Fields.FirstOrDefault(f => f.Name == "Value");
        if (valueField is not null)
        {
            return valueField.Value switch
            {
                decimal d => d > 50m ? (int)RiskLevel.High : (int)RiskLevel.Medium,
                int i => i > 50 ? (int)RiskLevel.High : (int)RiskLevel.Medium,
                double db => db > 50.0 ? (int)RiskLevel.High : (int)RiskLevel.Medium,
                _ => (int)RiskLevel.Medium
            };
        }

        return (int)RiskLevel.Medium;
    }

    /// <summary>
    /// Classifies a numeric score into a <see cref="RiskLevel"/>, clamping out-of-range values.
    /// </summary>
    public RiskLevel ClassifyScore(int score) => (RiskLevel)Math.Clamp(score, 1, 3);
}
