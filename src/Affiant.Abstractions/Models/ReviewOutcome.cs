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
    /// <param name="AmendmentsPreserved">
    /// True when a late reviewer decision's amendments — one that arrived after the entry was no
    /// longer Pending, e.g. a decision racing the expiry sweep — were persisted onto the
    /// DocketEntry despite the entry expiring (framework half of repo issue #8; see
    /// <c>ReviewGate.HandleDecisionAsync</c>'s restart path). False (default) for a plain timeout
    /// with no amendments to preserve, or for existing construction sites predating this flag.
    /// </param>
    public sealed record Expired(Guid DocketId, bool AmendmentsPreserved = false) : ReviewOutcome(DocketId);

    /// <summary>
    /// The approval policy escalated the review to a different reviewer or approval path.
    /// The <see cref="EscalationPath"/> identifies the target (e.g., a role, queue, or user ID).
    /// </summary>
    public sealed record Referral(Guid DocketId, string EscalationPath) : ReviewOutcome(DocketId);
}

/// <summary>
/// Result of <c>ReviewGate.FileForReviewAsync</c> — the non-blocking half of filing a review
/// (framework enabler for host issue affiant-host-apps#25 / triage F0-A1). Lets the host branch
/// on whether a human reviewer must act (<see cref="ReviewFilingResult.RequiresReview"/>) or the
/// review was already settled without a client round-trip
/// (<see cref="ReviewFilingResult.Decided"/>), instead of blocking the calling task on
/// <c>ReviewGate.FileReviewAsync</c>'s internal await.
/// </summary>
public abstract record ReviewFilingResult
{
    /// <summary>
    /// The Evidence Card was broadcast and awaits a reviewer decision. No waiter was registered —
    /// route the eventual decision to <c>ReviewGate.HandleDecisionAsync(EntryId, ...)</c> instead
    /// of blocking on it here.
    /// </summary>
    public sealed record RequiresReview(Guid EntryId) : ReviewFilingResult;

    /// <summary>
    /// The review was settled without a client round-trip — a StandingOrder auto-approval, a
    /// ReferralRequired escalation, or an idempotent replay of an entry that was already
    /// Approved/Rejected/Expired/Deferred when filed. <see cref="Outcome"/> carries the final
    /// <see cref="ReviewOutcome"/>.
    /// </summary>
    public sealed record Decided(ReviewOutcome Outcome) : ReviewFilingResult;
}
