namespace Affiant.Abstractions.Models;

/// <summary>
/// What an <see cref="Interfaces.IApprovalPolicy"/> says a proposed write needs before it may
/// execute — and, since the conformance release, <em>when</em> the window to say so closes.
///
/// <para>
/// <b>Why this type exists (protocol rule GT-4).</b> Until <c>1.0.0-beta.1</c> a policy returned a
/// bare <see cref="ReviewRequirement"/> and the deadline on the filed row came from one
/// process-wide default stamped <em>before</em> the policy chain ran. A host that wanted "five
/// minutes for a high-risk write, a day for a routine one" had nowhere to say it, and the rule the
/// gate is supposed to follow — the deadline is stamped from the policy result, after the chain —
/// could not even be expressed. A verdict is the seam that makes it expressible.
/// </para>
///
/// <para>
/// <b>The three degrade reasons.</b> A Standing Order is a write approved with no person present,
/// and three checks can hold one back (GT-5, PV-4): a field the entity requires with no known
/// value, a provenance grade the policy predicates on that points at nothing, and a host risk score
/// above the policy's ceiling. When one fires, <see cref="Requirement"/> reads
/// <see cref="ReviewRequirement.ReviewerConfirmation"/>, <see cref="DegradedFrom"/> reads
/// <see cref="ReviewRequirement.StandingOrder"/>, and <see cref="BlockedReason"/> carries the
/// stable code from <see cref="StandingOrderBlockedReasons"/>. Degrading <em>toward</em> a person is
/// always safe; the record has to say it happened, or a held-back Standing Order is indistinguishable
/// from a policy that simply asked for confirmation.
/// </para>
/// </summary>
/// <param name="Requirement">
/// The requirement in force — after the GT-5 and PV-4 checks have had their say, not the
/// requirement the policy first named. See <see cref="DegradedFrom"/>.
/// </param>
/// <param name="TimeToLive">
/// This write's review window, or <see langword="null"/> to fall through to the policy's own
/// declared default and then to the gate's (GT-4). Must be at least one millisecond: a zero or
/// negative window files an entry that reads expired on the read that files it, which no person can
/// ever decide, so the gate refuses it at evaluation with <c>wireup-invalid</c> rather than stamping it.
/// </param>
/// <param name="Reason">
/// Why, in one line, for the reviewer's card — or why the degrade happened. Field <em>names</em>
/// and grades only; never a field value.
/// </param>
/// <param name="BlockedReason">
/// The stable code for why a Standing Order was not honoured, or <see langword="null"/> when none
/// was held back. One of <see cref="StandingOrderBlockedReasons"/>. Separate from
/// <see cref="Reason"/> on purpose: a dashboard alerts on the code, and the sentence stays free to
/// be rewritten for whoever reads the card.
/// </param>
/// <param name="DegradedFrom">
/// The requirement the policy originally named, when one of the three checks degraded it, else
/// <see langword="null"/>.
/// </param>
/// <param name="PolicyId">
/// The policy that produced this verdict, stamped by the chain rather than by the policy itself so
/// it cannot be misreported. <see langword="null"/> only on the chain's own fallback verdict, which
/// no policy produced. A <see cref="ReviewRequirement.StandingOrder"/> verdict carries it into the
/// attestation the filing writes: a write approved with no person present still names who approved
/// it, and "the policy" is the honest answer.
/// </param>
/// <param name="PolicyVersion">
/// The version that policy declared when it fired, or <see langword="null"/> when it declares none.
/// Recorded so a later reader can tell what the policy said at the time rather than what it says
/// now.
/// </param>
public sealed record ApprovalVerdict(
    ReviewRequirement Requirement,
    TimeSpan? TimeToLive = null,
    string? Reason = null,
    string? BlockedReason = null,
    ReviewRequirement? DegradedFrom = null,
    string? PolicyId = null,
    string? PolicyVersion = null)
{
    /// <summary>
    /// A verdict that names a requirement and nothing else — the shape every <c>1.0.0-beta.1</c>
    /// policy returned. Present so that migrating a host policy is a change of return type and not a
    /// rewrite of every <c>return</c> statement in it.
    /// </summary>
    public static implicit operator ApprovalVerdict(ReviewRequirement requirement) => new(requirement);

    /// <summary>
    /// This verdict degraded to <see cref="ReviewRequirement.ReviewerConfirmation"/>, keeping its
    /// own <see cref="TimeToLive"/> — the degrade changes who decides, not when the window closes
    /// (PV-4, GT-5).
    /// </summary>
    public ApprovalVerdict DegradeToReviewer(string blockedReason, string reason) => this with
    {
        Requirement = ReviewRequirement.ReviewerConfirmation,
        DegradedFrom = Requirement,
        BlockedReason = blockedReason,
        Reason = reason,
    };
}

/// <summary>
/// The stable codes for why a Standing Order was not honoured (GT-5, PV-4), carried on
/// <see cref="ApprovalVerdict.BlockedReason"/> and on the <c>standing-order.blocked</c> telemetry
/// event's <c>blocked.reason</c> attribute. A code is never renamed or removed — an operator's
/// alert is keyed on it.
/// </summary>
public static class StandingOrderBlockedReasons
{
    /// <summary>A proposed field marked mandatory reads <see cref="ProvenanceSource.Empty"/> (GT-5).</summary>
    public const string MandatoryFieldEmpty = "mandatory-field-empty";

    /// <summary>
    /// A proposed field carries a tag the policy predicates on, graded above
    /// <see cref="ProvenanceSource.Conversation"/>, with no <see cref="ProvenanceBinding"/> (PV-4).
    /// </summary>
    public const string UnboundDeclaredInput = "unbound-declared-input";

    /// <summary>The host's risk score is above the Standing Order's declared threshold (GT-5).</summary>
    public const string RiskAboveThreshold = "risk-above-threshold";
}
