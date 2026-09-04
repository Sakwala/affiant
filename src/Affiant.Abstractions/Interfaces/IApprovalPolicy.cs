namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// One rule in the chain that decides <em>how</em> a proposed mutation must be approved before it
/// may execute — auto-approved under a standing order, confirmed by a reviewer, referred to someone
/// else, or escalated to multiple parties — and <em>how long</em> the window to say so stays open.
/// This is the primary extension point for encoding an organization's approval rules;
/// <c>Affiant.Policies</c> builds standing orders and referral rules on top of it.
/// </summary>
/// <remarks>
/// <para>
/// Policies compose as an ordered chain, not as a single winner: register any number of
/// implementations and <see cref="IApprovalPolicyEvaluator"/> walks them in registration order,
/// taking the first non-null answer. Returning <c>null</c> means "this rule has no opinion here" —
/// the normal case for a narrowly-scoped rule. If no policy claims the affidavit, the framework
/// falls back to <see cref="ReviewRequirement.ReviewerConfirmation"/>: the safe default is always a
/// human.
/// </para>
/// <para>
/// A policy decides the requirement and the review window only. It never performs the write, never
/// files the docket entry, and is never the place to enforce authorization of the acting user —
/// that is <see cref="IToolAuthorizationPolicy"/>'s job at the tool seam and
/// <see cref="IDecisionAuthorizationPolicy"/>'s at the decision, both enforced by the framework.
/// </para>
/// <para>
/// <b>Identity is supplied so a policy can bind, never so it can authorize.</b> Every evaluation
/// receives the <see cref="ConversationIdentity"/> of the turn that produced the proposal — the
/// conversation, the person, the tenant and the channel — so an order can say "only for this
/// member", "only inside this tenant", "only on our own web UI". That is binding: it decides what
/// the policy is <em>about</em>. Whether the principal who eventually presses approve is entitled
/// to do so is a different question the framework answers itself, before any transition, and it is
/// never delegated here. The two are separated because a policy that authorises the actor is a
/// policy every host has to get right, and an ownership check hand-rolled per host tends to check
/// the acting user and not the tenant, and to fall open when identity is unresolved.
/// </para>
/// <para>
/// <b>Two faults are refused at evaluation with nothing filed</b> (protocol rule CV-1): a verdict
/// carrying a <see cref="ApprovalVerdict.TimeToLive"/> that is not a review deadline, and an
/// <see cref="EvaluateAsync"/> that throws. Both surface as
/// <see cref="Exceptions.AffiantPolicyException"/> carrying <c>wireup-invalid</c>, with a
/// <c>policy.invalid</c> telemetry event naming which half of the contract broke. A policy that
/// cannot answer is a wiring the gate cannot run — the chain never falls through to a weaker
/// requirement because a policy failed.
/// </para>
/// </remarks>
public interface IApprovalPolicy
{
    /// <summary>
    /// Evaluate the approval requirement for the proposed mutation described by <paramref name="affidavit"/>.
    /// Return <c>null</c> to defer to the next policy in the evaluation chain.
    /// Return an <see cref="ApprovalVerdict"/> to terminate the chain with it — a bare
    /// <see cref="ReviewRequirement"/> converts implicitly, so a policy with nothing to say about
    /// the deadline still reads as one line.
    /// Implementations must be deterministic and stateless (no mutable fields).
    /// </summary>
    /// <param name="affidavit">The proposed write, as sworn.</param>
    /// <param name="identity">
    /// Where the proposal came from — the conversation, the person whose turn produced it, the
    /// tenant and the channel. For <em>binding</em> only; see the interface's remarks.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    Task<ApprovalVerdict?> EvaluateAsync(
        Affidavit affidavit,
        ConversationIdentity identity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The provenance sources this policy predicates on (protocol rule PV-4). Empty — the default —
    /// for a policy that looks only at field values or host state, which the rule leaves alone.
    ///
    /// <para>
    /// Before a <see cref="ReviewRequirement.StandingOrder"/> verdict from this policy is honoured,
    /// the chain checks that every proposed field whose tag in force names one of these sources
    /// <em>and</em> is graded above <see cref="ProvenanceSource.Conversation"/> points at something
    /// an auditor could re-check. If any does not, the verdict degrades to
    /// <see cref="ReviewRequirement.ReviewerConfirmation"/> and this policy's
    /// <see cref="DefaultTimeToLive"/> still applies: the degrade changes who decides, not when the
    /// window closes. Declaring nothing is not a way around the rule — it is the honest answer for a
    /// policy that predicates on nothing outside the conversation.
    /// </para>
    /// </summary>
    IReadOnlyCollection<ProvenanceSource> DeclaredInputs => [];

    /// <summary>
    /// This policy's identity, on the <c>policy.id</c> telemetry attribute and — when it approves a
    /// write with no person present — in the attestation the Docket row carries. Defaults to the
    /// concrete type's full name, which is stable across releases in a way a display name is not.
    /// Override it when a host names its policies in configuration and wants both keyed on that name.
    /// </summary>
    /// <remarks>
    /// A Standing Order's approval is attributed to the policy that fired, because there is nobody
    /// else to attribute it to (AZ-1). The chain stamps this onto the verdict rather than trusting a
    /// policy to report itself: a record of who approved a write has to be the framework's answer.
    /// </remarks>
    string PolicyId => GetType().FullName ?? GetType().Name;

    /// <summary>
    /// This policy's own version, recorded alongside <see cref="PolicyId"/>, or
    /// <see langword="null"/> when the policy does not version itself. Override it when a host
    /// revises a policy's rules and needs to tell an approval made under the old rules from one made
    /// under the new — a question an attestation written months ago is the only place to answer.
    /// </summary>
    string? PolicyVersion => null;

    /// <summary>
    /// This policy's own review window, used when its verdict names none (protocol rule GT-4), or
    /// <see langword="null"/> to fall through to <c>AffiantCoreOptions.DefaultDocketTtl</c>. Held to
    /// the same rule as <see cref="ApprovalVerdict.TimeToLive"/>: at least one millisecond, and
    /// stampable. A policy that declares an unusable one is refused when the chain reaches it, with
    /// nothing filed.
    /// </summary>
    TimeSpan? DefaultTimeToLive => null;
}
