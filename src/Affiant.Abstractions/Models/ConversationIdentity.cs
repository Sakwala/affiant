namespace Affiant.Abstractions.Models;

/// <summary>
/// Where a proposal came from: the conversation, the person whose turn produced it, the tenant it
/// is scoped to and the channel it arrived on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Supplied to an approval policy so it can bind, never so it can authorize.</b> A standing
/// order that fires only for a named member, only inside one tenant, or only on the host's own web
/// UI needs to know those things to <em>express itself</em> — that is binding, and it is what this
/// record is for. Deciding whether the acting principal is entitled to approve a row is a different
/// question, enforced by the framework through <see cref="Interfaces.IDecisionAuthorizationPolicy"/>
/// before any transition, and never delegated to a policy. A policy that treats this record as
/// permission has confused "who is this for" with "who may say yes".
/// </para>
/// <para>
/// It is also not a principal. A policy runs at <em>filing</em> time, when nobody has decided
/// anything yet; the identity here is the turn's, not the eventual decider's, and the two are
/// frequently different people.
/// </para>
/// </remarks>
/// <param name="SessionId">The conversation the proposal came from.</param>
/// <param name="UserId">The person whose turn produced the proposal.</param>
/// <param name="StartedAt">When the conversation began.</param>
/// <param name="HostAppName">The host application's own name for itself, when it supplies one.</param>
/// <param name="TenantId">
/// The tenant the proposal is scoped to. A policy that must not fire outside one organisation
/// reads it here rather than inferring it from a field value.
/// </param>
/// <param name="Channel">
/// The channel the turn arrived on — the host's own name for it (its web UI, a chat relay, a
/// queue). A standing order that trusts one surface and not another has nowhere else to say so.
/// </param>
public record ConversationIdentity(
    string SessionId,
    string UserId,
    DateTimeOffset StartedAt,
    string? HostAppName = null,
    string? TenantId = null,
    string? Channel = null);
