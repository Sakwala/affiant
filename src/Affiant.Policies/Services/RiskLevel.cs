namespace Affiant.Policies.Services;

/// <summary>
/// Risk classification for approval policies. Matches Phase 1 R1/R2/R3 schema.
/// </summary>
public enum RiskLevel
{
    /// <summary>
    /// Low risk: high-frequency, low-impact operations. Auto-approvable by Standing Orders.
    /// </summary>
    Low = 1,

    /// <summary>
    /// Medium risk: moderate impact, business-logic dependent. Requires confirmation.
    /// </summary>
    Medium = 2,

    /// <summary>
    /// High risk: high-value, cross-entity, or irreversible. Escalation required.
    /// </summary>
    High = 3
}
