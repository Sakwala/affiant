namespace Affiant.Abstractions.Models;

/// <summary>
/// What a relay asserts about the message it is carrying: which identity on its channel the
/// message came from, and the id of the message itself.
/// </summary>
/// <remarks>
/// A relay is a trusted machine caller that <em>asserts</em> a person's identity rather than
/// authenticating them. Both fields are carried onto the attestation of any decision made through
/// the relay, so a reader of the record can name the message a write came from.
/// </remarks>
/// <param name="ChannelIdentity">
/// How the person is addressed on the channel the relay speaks for — a workspace member id, a
/// phone number, an address, whatever the relay's own directory uses. Opaque to the gate: carried
/// onto the record and compared for equality, never parsed.
/// </param>
/// <param name="MessageId">The relay's id for the message that carried the request.</param>
public sealed record RelayAssertion(string ChannelIdentity, string MessageId);

/// <summary>
/// Who is acting — the resolved identity behind a decision, an execution report or a resubmission.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Member"/> is a human-verified session: the host authenticated the person itself.
/// <see cref="Service"/> is a machine caller — a relay, a queue consumer, a scheduled job. A
/// service principal may name the person it is speaking for (<see cref="Service.AssertedMember"/>)
/// and the message it is carrying (<see cref="Service.Relay"/>), but a machine caller can
/// <b>never</b> produce a <see cref="Attestor.Member"/> attestation: the strongest attestation a
/// relayed decision can carry is <see cref="Attestor.MemberViaRelay"/>, which names both the person
/// and the relay.
/// </para>
/// <para>
/// <c>null</c> in <see cref="DecisionContext.Principal"/> means <em>unresolved</em>, which is not
/// the same as anonymous: a decision on an unresolved principal is refused before the Docket is
/// read. "Identity unknown" is never "allow".
/// </para>
/// <para>
/// The hierarchy is closed — the constructor is <c>private protected</c>, so the only principal
/// kinds that exist are the two nested here and no host can invent a third that the attestation
/// factories have no rule for.
/// </para>
/// </remarks>
public abstract record Principal
{
    private protected Principal() { }

    /// <summary>The wire discriminator for this principal kind — <c>member</c> or <c>service</c>.</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// The host's id for whoever or whatever is acting, whichever kind this is. Read for telemetry
    /// and for the record; never for a decision about what this principal may do, which is
    /// <c>Kind</c>'s business and the host authorization port's.
    /// </summary>
    public string PrincipalId => this switch
    {
        Member member => member.Id,
        Service service => service.Id,
        _ => throw new InvalidOperationException(
            $"unreachable: the Principal hierarchy is closed and {GetType().Name} is not one of it"),
    };

    /// <summary>A human-verified session: the host authenticated this person itself.</summary>
    /// <param name="Id">The host's id for the person.</param>
    public sealed record Member(string Id) : Principal
    {
        /// <inheritdoc/>
        public override string Kind => "member";
    }

    /// <summary>
    /// A machine caller: a relay, a queue consumer, a scheduled job.
    /// </summary>
    /// <param name="Id">The host's id for the calling service.</param>
    /// <param name="Relay">
    /// The message this service is carrying, when it is a relay. <c>null</c> when the service is
    /// acting on its own behalf.
    /// </param>
    /// <param name="AssertedMember">
    /// The person this service says it is speaking for. An assertion, not an authentication — it
    /// never upgrades the principal to <see cref="Member"/>.
    /// </param>
    public sealed record Service(
        string Id,
        RelayAssertion? Relay = null,
        string? AssertedMember = null) : Principal
    {
        /// <inheritdoc/>
        public override string Kind => "service";
    }
}

/// <summary>
/// Everything the gate is allowed to know about who is deciding and where they are deciding from —
/// passed at the call site, never resolved from ambient state.
/// </summary>
/// <remarks>
/// <para>
/// Bundled into one record rather than added as five parameters so the decision path can grow the
/// facts it carries without a source break at every host call site each time. Every entry point on
/// the gate's decision surface — the decision itself, the execution report and a resubmission —
/// takes one, and each runs the same checks against it.
/// </para>
/// <para>
/// <b>There is no unattributed context.</b> A <see cref="Principal"/> of <c>null</c> is refused
/// with <c>decision-unauthorized</c> before the Docket is read, and a <see cref="TenantId"/> is
/// required by the constructor: the framework compares the row's tenant with the caller's itself
/// rather than trusting a store's scope, and a caller that names no tenant has not said which rows
/// it may see. Both are the fail-closed direction, and neither has an overload that skips it.
/// </para>
/// </remarks>
/// <param name="Principal">
/// Who is acting, or <c>null</c> when the host could not resolve an identity — refused, never
/// treated as permission.
/// </param>
/// <param name="TenantId">
/// The tenant the caller is acting in. An entry outside it is <em>not found</em>, never
/// "forbidden": telling a caller that an id they may not touch exists is the leak the tenant check
/// is for.
/// </param>
/// <param name="ConversationId">
/// The conversation the act arrived on, for the host's telemetry. Never used to authorize.
/// </param>
/// <param name="Channel">
/// The channel the act arrived on — the host's own name for it. Carried so a policy can bind and a
/// host can tell a decision made in its own UI from one relayed off a chat channel.
/// </param>
/// <param name="Reason">The reviewer's stated reason, recorded on the row.</param>
/// <remarks>
/// <b>There is no instant on this record</b> (AZ-1). When a decision was made is the gate's own
/// observation, read from its injected clock, and it is stamped onto the attestation and the
/// decision record from there. A caller-supplied instant would make the <c>at</c> of an attestation
/// worth exactly what the calling host is worth: it was accepted unvalidated, so an attestation
/// could be dated five years before the row it attests to was filed. The record is what the
/// implementation observed, not what the caller said.
/// </remarks>
public sealed record DecisionContext(
    Principal? Principal,
    string TenantId,
    string? ConversationId = null,
    string? Channel = null,
    string? Reason = null);
