namespace Affiant.Abstractions.Models;

/// <summary>
/// Discriminated union representing the final outcome of a review filed through the ReviewGate.
/// Matches framework specification §2.7 review state machine outcomes.
/// </summary>
public abstract record ReviewOutcome(Guid DocketId)
{
    /// <summary>The reviewer (or StandingOrder policy) approved the proposed write.</summary>
    public sealed record Approved(Guid DocketId) : ReviewOutcome(DocketId);

    /// <summary>The reviewer explicitly rejected the proposed write.</summary>
    public sealed record Rejected(Guid DocketId, string Reason = "No reason provided") : ReviewOutcome(DocketId);

    /// <summary>No reviewer response arrived within the timeout window.</summary>
    public sealed record Expired(Guid DocketId) : ReviewOutcome(DocketId);

    /// <summary>
    /// The approval policy escalated the review to a different reviewer or approval path.
    /// The <see cref="EscalationPath"/> identifies the target (e.g., a role, queue, or user ID).
    /// </summary>
    public sealed record Referral(Guid DocketId, string EscalationPath) : ReviewOutcome(DocketId);
}
