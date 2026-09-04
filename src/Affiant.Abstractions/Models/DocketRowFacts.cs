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
/// that exist are the three nested here and no code path can invent a fourth mode.
/// </para>
/// <para>
/// <b>The rule is structural, not a convention.</b> Every arm's own constructor is private, and the
/// only way to build one is a factory that takes the thing entitled to produce it:
/// <see cref="Member.Of(Principal.Member)"/> takes a human-verified session and nothing else, so
/// there is no overload, no optional parameter and no <c>with</c> expression through which a
/// machine caller reaches a <c>member</c> attestation — a compiler rejects the shortcut before a
/// reviewer has to notice it. <see cref="MemberViaRelay.Of(Principal.Service)"/> takes a service
/// principal and returns <c>null</c> unless it carries <em>both</em> the person it speaks for and
/// the relay assertion that carried them, which is the strongest thing a machine can honestly say.
/// <see cref="StandingOrder"/> is not reachable from a principal at all: a policy verdict writes
/// that one, in the same operation as the filing.
/// </para>
/// </remarks>
public abstract record Attestor
{
    private protected Attestor() { }

    /// <summary>The wire discriminator for this attestor kind.</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// The strongest attestation <paramref name="principal"/> can honestly make, or <c>null</c> when
    /// it can make none.
    /// </summary>
    /// <remarks>
    /// A member principal attests <see cref="Member"/>. A service principal carrying both a relay
    /// assertion and the person it speaks for attests <see cref="MemberViaRelay"/>, naming both —
    /// and that is the <em>only</em> thing it can attest. A service principal with nothing to relay
    /// attests nothing: it is a machine acting on its own behalf, and a machine cannot agree to a
    /// write in a person's name.
    /// </remarks>
    public static Attestor? For(Principal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal switch
        {
            Principal.Member member => Member.Of(member),
            Principal.Service service => MemberViaRelay.Of(service),
            _ => null,
        };
    }

    /// <summary>Who this attestation is about: the person, however they reached the gate.</summary>
    public string Subject => this switch
    {
        Member member => member.Id,
        MemberViaRelay relayed => relayed.MemberId,
        StandingOrder order => order.PolicyId,
        _ => throw new InvalidOperationException(
            $"unreachable: the Attestor hierarchy is closed and {GetType().Name} is not one of it"),
    };

    /// <summary>A human-verified session decided this entry — the only claim a machine caller can never make.</summary>
    public sealed record Member : Attestor
    {
        private Member(string id) => Id = id;

        /// <summary>The host's id for the person.</summary>
        public string Id { get; }

        /// <inheritdoc/>
        public override string Kind => "member";

        /// <summary>
        /// The attestation a human-verified session makes. The parameter type is the enforcement:
        /// there is no path from a <see cref="Principal.Service"/> to this record.
        /// </summary>
        public static Member Of(Principal.Member principal)
        {
            ArgumentNullException.ThrowIfNull(principal);
            return new Member(principal.Id);
        }

        /// <summary>
        /// Reads back an attestation a store already holds. Not a second way to <em>make</em> one:
        /// it takes an id and no principal at all, so there is still no expression anywhere that
        /// turns a <see cref="Principal.Service"/> into a member attestation. Reconstructing a
        /// record that was written under the rule is not the same act as writing one that was not.
        /// </summary>
        public static Member FromStorage(string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            return new Member(id);
        }
    }

    /// <summary>
    /// A person decided this entry through a trusted relay — a machine caller that asserted their
    /// identity rather than authenticating them. Both the person and the relay are named, because
    /// the record must not read as though the person signed in directly.
    /// </summary>
    public sealed record MemberViaRelay : Attestor
    {
        private MemberViaRelay(string memberId, AttestationRelay relay)
        {
            MemberId = memberId;
            Relay = relay;
        }

        /// <summary>The host's id for the person the relay named.</summary>
        public string MemberId { get; }

        /// <summary>The relay, and the message the decision arrived on.</summary>
        public AttestationRelay Relay { get; }

        /// <inheritdoc/>
        public override string Kind => "member-via-relay";

        /// <summary>
        /// The strongest attestation a machine caller can make, or <c>null</c> when it has nothing
        /// to relay — no asserted member, or no relay assertion naming the message the decision
        /// arrived on. A service with neither is acting on its own behalf and attests nothing.
        /// </summary>
        public static MemberViaRelay? Of(Principal.Service principal)
        {
            ArgumentNullException.ThrowIfNull(principal);
            if (principal.Relay is not { } relay) return null;
            if (principal.AssertedMember is not { Length: > 0 } memberId) return null;

            return new MemberViaRelay(
                memberId,
                new AttestationRelay(principal.Id, relay.ChannelIdentity, relay.MessageId));
        }

        /// <inheritdoc cref="Member.FromStorage(string)"/>
        public static MemberViaRelay FromStorage(string memberId, AttestationRelay relay)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
            ArgumentNullException.ThrowIfNull(relay);
            return new MemberViaRelay(memberId, relay);
        }
    }

    /// <summary>
    /// A policy approved this entry with no person present, and the pipeline writes this attestation
    /// in the same operation that files the entry approved — there is no window in which an approved
    /// write has no attribution.
    /// </summary>
    public sealed record StandingOrder : Attestor
    {
        private StandingOrder(string policyId, string version)
        {
            PolicyId = policyId;
            Version = version;
        }

        /// <summary>The host's id for the policy that fired.</summary>
        public string PolicyId { get; }

        /// <summary>The version of that policy, so a later reader can tell what it said at the time.</summary>
        public string Version { get; }

        /// <inheritdoc/>
        public override string Kind => "standing-order";

        /// <summary>
        /// The attestation the pipeline writes when a policy approves with no person present. Not
        /// reachable from a <see cref="Principal"/>: nobody decided, so there is nobody to name.
        /// </summary>
        /// <param name="policyId">The policy that fired.</param>
        /// <param name="version">
        /// Its version, or <c>null</c> when the policy declares none — recorded as
        /// <see cref="Unversioned"/> rather than left blank, so a reader can tell "this policy does
        /// not version itself" from "the version was lost".
        /// </param>
        public static StandingOrder Of(string policyId, string? version)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
            return new StandingOrder(policyId, version is { Length: > 0 } v ? v : Unversioned);
        }

        /// <inheritdoc cref="Member.FromStorage(string)"/>
        public static StandingOrder FromStorage(string policyId, string version)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
            return new StandingOrder(policyId, version);
        }

        /// <summary>What is recorded for a policy that declares no version of its own.</summary>
        public const string Unversioned = "unversioned";
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
