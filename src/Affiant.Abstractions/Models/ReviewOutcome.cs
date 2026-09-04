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

    /// <summary>
    /// The gate refused to carry this act, and named why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refusal is not an expiry and not a rejection: nobody rejected the write and nothing ran out
    /// of time. Reporting all three as <see cref="Expired"/> — which is what the gate did before
    /// this arm existed — told a host that a deadline passed when what actually happened was that a
    /// second decision arrived on an already-decided entry, or that a joint approval requirement the
    /// framework does not implement blocked the entry outright. The three lead to different host
    /// behaviour, so they are three answers.
    /// </para>
    /// <para>
    /// <see cref="Code"/> is one of <see cref="DocketRefusalCodes"/>; <see cref="Detail"/> carries
    /// whatever that code makes meaningful — the blocked requirement level, the uncovered tool.
    /// </para>
    /// </remarks>
    /// <param name="DocketId">The entry the refusal is about.</param>
    /// <param name="Code">Why, as one of <see cref="DocketRefusalCodes"/>.</param>
    /// <param name="Detail">The context that code makes meaningful, or <c>null</c>.</param>
    public sealed record Refused(Guid DocketId, string Code, string? Detail = null)
        : ReviewOutcome(DocketId);
}

/// <summary>
/// Why the gate refused an act on a Docket entry — the framework's half of the protocol's refusal
/// registry, as it reaches a host through <see cref="ReviewOutcome.Refused"/>.
/// </summary>
/// <remarks>
/// Codes, not messages: a host branches on these, logs them and shows them, and a string a human
/// wrote is a string a later human will rewrite. Each constant's documentation is what the code
/// means; the message a host shows is the host's to write.
/// </remarks>
public static class DocketRefusalCodes
{
    /// <summary>
    /// A decision arrived on an entry that is no longer pending — it was already approved or
    /// rejected, or it carries a blocked marker and never accepts one.
    /// </summary>
    public const string DecisionNotPending = "decision-not-pending";

    /// <summary>
    /// A decision arrived on an entry that was pending when the caller read it and was decided by
    /// somebody else before the guarded write landed. Distinct from
    /// <see cref="DecisionNotPending"/>: this caller was not late to the entry, it was late to the
    /// race.
    /// </summary>
    public const string DecisionLostRace = "decision-lost-race";

    /// <summary>
    /// A decision arrived after the entry's deadline. The boundary is inclusive: a decision landing
    /// exactly on the deadline is late. Amendments such a decision carried are preserved on the row
    /// for a resubmission.
    /// </summary>
    public const string DecisionExpired = "decision-expired";

    /// <summary>
    /// The policy chain returned a requirement level this version records but does not run. The
    /// entry is filed pending with a blocked marker, refuses every decision, and is never degraded
    /// to a weaker requirement. <see cref="ReviewOutcome.Refused.Detail"/> names the level.
    /// </summary>
    public const string RequirementNotImplemented = "requirement-not-implemented";

    /// <summary>
    /// The proposal came from a write-capable tool the host declared the gate cannot intercept. The
    /// entry is recorded — blocked, never silently allowed to write.
    /// <see cref="ReviewOutcome.Refused.Detail"/> names the tool.
    /// </summary>
    public const string CoverageRefused = "coverage-refused";

    /// <summary>The entry does not exist, or does not exist in the caller's tenant, which is the same answer.</summary>
    public const string EntryNotFound = "entry-not-found";

    /// <summary>A second execution report arrived for a row whose outcome is already on the record.</summary>
    public const string ExecutionAlreadyRecorded = "execution-already-recorded";
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
