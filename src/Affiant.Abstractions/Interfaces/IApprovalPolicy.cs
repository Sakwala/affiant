namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// One rule in the chain that decides <em>how</em> a proposed mutation must be approved before it
/// may execute — auto-approved under a standing order, confirmed by a reviewer, referred to someone
/// else, or escalated to multiple parties. This is the primary extension point for encoding an
/// organization's approval rules; <c>Affiant.Policies</c> builds standing orders and referral rules
/// on top of it.
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
/// A policy decides the requirement only. It never performs the write, never files the docket
/// entry, and is never the place to enforce authorization of the acting user — that is
/// <see cref="IToolAuthorizationPolicy"/>'s job, evaluated earlier.
/// </para>
/// </remarks>
public interface IApprovalPolicy
{
    /// <summary>
    /// Evaluate the approval requirement for the proposed mutation described by <paramref name="affidavit"/>.
    /// Return <c>null</c> to defer to the next policy in the evaluation chain.
    /// Return a <see cref="ReviewRequirement"/> to terminate the chain with that value.
    /// Implementations must be deterministic and stateless (no mutable fields).
    /// </summary>
    Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default);
}
