namespace Affiant.Abstractions.Models;

/// <summary>
/// The protocol tag this build's wire shapes and Docket rows are pinned to.
/// </summary>
/// <remarks>
/// Stamped onto every <see cref="DocketEntry"/> at filing so a row read years later can say which
/// version of the shapes it was written under, rather than being interpreted under whatever the
/// reader happens to be running.
/// </remarks>
public static class AffiantProtocol
{
    /// <summary>The protocol version tag — the <c>0.1.0</c> schema set.</summary>
    public const string Version = "0.1.0";
}

/// <summary>
/// What became of an approved write, once the host's executor reported.
/// </summary>
/// <remarks>
/// <para>
/// A separate axis from <see cref="ReviewStatus"/> rather than two more statuses because an
/// approved-but-failed write and an approved-and-committed one differ in what the <em>host</em>
/// must do next, not in whether the approval happened; collapsing them into the status loses the
/// approval, and an approved-but-failed write must stay distinguishable from an
/// approved-and-committed one on the row.
/// </para>
/// <para>
/// The framework never performs the write — the only path to <see cref="Executed"/> is the host's
/// report through <see cref="Interfaces.IDocketStore.RecordExecutionAsync"/>, recorded once under a
/// guard. An execution state that can be flipped after the fact is an audit record that lies.
/// </para>
/// </remarks>
public enum ExecutionOutcome
{
    /// <summary>Approved, and the host's executor has not reported.</summary>
    Unexecuted,

    /// <summary>The host reported the write committed.</summary>
    Executed,

    /// <summary>The host reported the write failed. The approval still happened.</summary>
    Failed
}

/// <summary>What a reviewer chose. Amending is approving with an amendment map, not a third kind.</summary>
public enum DecisionKind
{
    /// <summary>The reviewer approved the proposed write, with or without amendments.</summary>
    Approve,

    /// <summary>The reviewer refused it.</summary>
    Reject
}

/// <summary>
/// What a reviewer decided, as it is recorded on the row.
/// </summary>
/// <remarks>
/// Separate from <see cref="Models.Attestation"/> because they answer different questions: the
/// attestation says <em>who may be held to this</em>, the decision says <em>what they chose and
/// why</em>. A Standing Order produces an attestation and no decision record — no person chose
/// anything.
/// </remarks>
/// <param name="Kind">Approve or reject.</param>
/// <param name="Reason">The reviewer's stated reason, or <c>null</c> when they gave none.</param>
/// <param name="At">When the decision was made.</param>
public sealed record DecisionRecord(DecisionKind Kind, string? Reason, DateTimeOffset At);

/// <summary>What a relay asserted, as it is written onto an attestation.</summary>
/// <param name="Principal">The host's id for the relay service that carried the decision.</param>
/// <param name="ChannelIdentity">The identity, on the relay's own channel, the decision came from.</param>
/// <param name="MessageId">The relay's id for the message that carried the decision.</param>
public sealed record AttestationRelay(string Principal, string ChannelIdentity, string MessageId);

/// <summary>
/// Who agreed to a write. The <em>mode</em> is the kind of attestor; there is no separate mode
/// field for it to drift from.
/// </summary>
/// <remarks>
/// <para>
/// The three arms encode what identity may attest what. A human-verified session attests
/// <see cref="Member"/>. A machine caller may <b>never</b> attest <see cref="Member"/>: a decision a
/// person makes through a trusted relay attests <see cref="MemberViaRelay"/>, naming both the person
/// and the relay, and a capture a policy auto-approves attests <see cref="StandingOrder"/>, naming
/// the policy and the version of it that fired.
/// </para>
/// <para>
/// The hierarchy is closed — the constructor is <c>private protected</c>, so the only attestor kinds
/// that exist are the three nested here. Which principal is <em>permitted</em> to produce which arm
/// is enforced where decisions are authorized, not here; this type fixes the shape so no code path
/// can invent a fourth mode.
/// </para>
/// </remarks>
public abstract record Attestor
{
    private protected Attestor() { }

    /// <summary>The wire discriminator for this attestor kind.</summary>
    public abstract string Kind { get; }

    /// <summary>A human-verified session decided this entry — the only claim a machine caller can never make.</summary>
    /// <param name="Id">The host's id for the person.</param>
    public sealed record Member(string Id) : Attestor
    {
        /// <inheritdoc/>
        public override string Kind => "member";
    }

    /// <summary>
    /// A person decided this entry through a trusted relay — a machine caller that asserted their
    /// identity rather than authenticating them. Both the person and the relay are named, because
    /// the record must not read as though the person signed in directly.
    /// </summary>
    /// <param name="MemberId">The host's id for the person the relay named.</param>
    /// <param name="Relay">The relay, and the message the decision arrived on.</param>
    public sealed record MemberViaRelay(string MemberId, AttestationRelay Relay) : Attestor
    {
        /// <inheritdoc/>
        public override string Kind => "member-via-relay";
    }

    /// <summary>
    /// A policy approved this entry with no person present, and the pipeline writes this attestation
    /// in the same operation that files the entry approved — there is no window in which an approved
    /// write has no attribution.
    /// </summary>
    /// <param name="PolicyId">The host's id for the policy that fired.</param>
    /// <param name="Version">The version of that policy, so a later reader can tell what it said at the time.</param>
    public sealed record StandingOrder(string PolicyId, string Version) : Attestor
    {
        /// <inheritdoc/>
        public override string Kind => "standing-order";
    }
}

/// <summary>
/// The attestation record every write that reaches an executor carries: who agreed, when, and to
/// which entry. An implementation that cannot attribute a write refuses it.
/// </summary>
/// <remarks>
/// <see cref="EntryId"/> is repeated here rather than left implicit because the attestation is the
/// fragment a host exports, signs or ships to an audit sink; a record that cannot name its own
/// subject is not evidence.
/// </remarks>
/// <param name="By">Who agreed.</param>
/// <param name="At">When they agreed.</param>
/// <param name="EntryId">The Docket entry this attests to.</param>
public sealed record Attestation(Attestor By, DateTimeOffset At, Guid EntryId);

/// <summary>The category of write-capable tool the gate cannot intercept.</summary>
public enum CoverageCategory
{
    /// <summary>A write-capable tool with no execute step for the gate to replace.</summary>
    NoExecute,

    /// <summary>A tool the model provider executes on its own side.</summary>
    ProviderExecuted,

    /// <summary>A hosted MCP server-side write.</summary>
    HostedMcp
}

/// <summary>
/// Why an entry cannot be decided even though it sits in <see cref="ReviewStatus.Pending"/>.
/// </summary>
/// <remarks>
/// <para>
/// An implementation that receives a requirement level it does not run records that level verbatim,
/// files the entry pending with this marker, refuses every decision on it, never executes it, and
/// <b>never degrades it to a weaker requirement</b> — a joint requirement quietly satisfied by one
/// approval is the failure this exists to prevent. A blocked entry's card says so on its face and
/// never claims a confirmation is being awaited.
/// </para>
/// <para>
/// Discriminated on <see cref="Code"/>, and each arm carries exactly the context that code makes
/// meaningful: a coverage refusal has no requirement level to report.
/// </para>
/// </remarks>
public abstract record BlockedMarker
{
    private protected BlockedMarker() { }

    /// <summary>The refusal code this marker records.</summary>
    public abstract string Code { get; }

    /// <summary>
    /// A requirement level this version recognises but does not run reached the pipeline —
    /// <see cref="ReviewRequirement.ReferralRequired"/> or <see cref="ReviewRequirement.MultiParty"/>,
    /// whose semantics are reserved.
    /// </summary>
    /// <param name="Level">The requirement level that is not implemented, recorded verbatim.</param>
    public sealed record RequirementNotImplemented(ReviewRequirement Level) : BlockedMarker
    {
        /// <inheritdoc/>
        public override string Code => "requirement-not-implemented";
    }

    /// <summary>
    /// A proposal came from a write-capable tool the host declared the gate cannot intercept. Its
    /// proposals are still recorded — blocked, never silently allowed to write — and the tool name is
    /// on the row so coverage can be re-assessed on a resubmission.
    /// </summary>
    /// <param name="Category">The category the gate cannot cover.</param>
    /// <param name="ToolName">The tool the uncovered proposal came from.</param>
    public sealed record CoverageRefused(CoverageCategory Category, string ToolName) : BlockedMarker
    {
        /// <inheritdoc/>
        public override string Code => "coverage-refused";
    }
}

/// <summary>
/// The amendments a decision carried after the entry had already expired, with the act that carried
/// them.
/// </summary>
/// <remarks>
/// The instant and the principal are here, and not merely implied, because a resubmission prefills
/// these values as <b>a person's own correction</b>: each prefilled field is tagged as user-stated
/// with a reviewer-act binding, and that binding names the decision the correction was made on.
/// Without the instant the binding would have to point at the row's deadline — the moment the gate
/// refused, not the moment the person typed — and without the principal the record could not say
/// whose correction it is.
/// </remarks>
/// <param name="Amendments">The map the refused decision carried. A <c>null</c> value clears the field; an absent key leaves it untouched.</param>
/// <param name="At">When the refused decision was made.</param>
/// <param name="By">Who made it, as the host identifies them.</param>
public sealed record PreservedAmendments(
    IReadOnlyDictionary<string, object?> Amendments,
    DateTimeOffset At,
    string By);

/// <summary>
/// What this entry replaces and what replaced it.
/// </summary>
/// <remarks>
/// A resubmission is a <b>new</b> entry, never a reopened one: the superseded entry keeps its
/// terminal state and records its successor, so the history reads forward and nothing that was once
/// decided is quietly edited. An entry that is not expired cannot be resubmitted.
/// </remarks>
/// <param name="Supersedes">The entry this one resubmits, or <c>null</c> for a first filing.</param>
/// <param name="SupersededBy">The entry that resubmitted this one, or <c>null</c> while none has.</param>
public sealed record Lineage(Guid? Supersedes, Guid? SupersededBy);
