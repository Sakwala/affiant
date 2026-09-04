namespace Affiant.Core.Services;

using Affiant.Abstractions.Models;
using Affiant.Core.Observability;

/// <summary>
/// The two host-independent checks that hold a Standing Order back, wired to the telemetry key an
/// operator alerts on: a proposed field the write requires with no known value (protocol rule GT-5),
/// then a provenance grade the policy predicates on that points at nothing (protocol rule PV-4).
///
/// <para>
/// <b>Why the order is fixed.</b> The mandatory-<c>Empty</c> check runs first because it is the
/// cheapest read and the least conditional — it depends on nothing the policy declared and nothing a
/// host port returns, so a proposal with a hole in it degrades identically under every wiring. PV-4
/// runs next because it is still a pure read of the Affidavit, and there is no reason to spend a
/// host's risk scorer on a verdict that is already going to a person. The risk comparison is third
/// and lives with the Standing Order base class that owns the ceiling: the framework ships no
/// scoring formula (GT-5).
/// </para>
///
/// <para>
/// <b>Why both the base class and the chain run these.</b> <c>StandingOrderBase</c> runs them
/// before it calls a host's scorer, so a proposal missing a required field never costs a score. The
/// approval-policy chain runs them again over whatever verdict reaches it, because a host may
/// implement <see cref="Abstractions.Interfaces.IApprovalPolicy"/> directly and return a Standing
/// Order without inheriting anything — and the rule is that <em>the gate</em> checks before honouring
/// such a verdict, not that a particular base class does. For a verdict that already passed, the
/// second pass is a pure read that changes nothing and emits nothing.
/// </para>
///
/// <para>
/// More than one check can be true of the same proposal. The first to fire is the one the record
/// names, and the verdict degrades exactly once.
/// </para>
/// </summary>
public static class StandingOrderGuardrails
{
    /// <summary>
    /// The risk comparison (GT-5): a Standing Order that declares a ceiling fires only when the
    /// host's score is at or below it, and is otherwise degraded to reviewer confirmation with the
    /// reason a person is being asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The framework owns the comparison; the host owns the number.</b> This is the one
    /// implementation of it, and the one sentence that says why the order did not fire — a policy
    /// written against the bare <c>IApprovalPolicy</c> and one built on <c>StandingOrderBase</c>
    /// degrade identically, and a reviewer reading the card sees the same explanation either way.
    /// </para>
    /// <para>
    /// The stable code is a separate attribute from the sentence: a dashboard alerts on the code,
    /// and the sentence stays free to be rewritten for whoever reads the card.
    /// </para>
    /// </remarks>
    /// <param name="verdict">The Standing Order verdict under comparison.</param>
    /// <param name="riskScore">The host's score for this write.</param>
    /// <param name="threshold">The ceiling the order declared.</param>
    /// <param name="policyId">The policy, for the telemetry event.</param>
    /// <param name="policyVersion">Its version, or <c>null</c>.</param>
    /// <returns>
    /// The verdict carrying the score when it fires; a degraded one carrying the reason when it does
    /// not.
    /// </returns>
    public static ApprovalVerdict ApplyRiskCeiling(
        ApprovalVerdict verdict,
        int riskScore,
        int threshold,
        string policyId,
        string? policyVersion = null)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(policyId);

        if (riskScore <= threshold)
            return verdict with { RiskScore = riskScore };

        var overThreshold =
            $"GT-5: the host's risk score ({riskScore}) is above the Standing Order's threshold " +
            $"({threshold}), so a person still confirms this write.";

        AffiantTelemetry.RecordStandingOrderBlocked(
            policyId,
            blockedReason: StandingOrderBlockedReasons.RiskAboveThreshold,
            reason: overThreshold,
            riskScore: riskScore,
            riskThreshold: threshold,
            policyVersion: policyVersion);

        return verdict
            .DegradeToReviewer(StandingOrderBlockedReasons.RiskAboveThreshold, overThreshold)
            with { RiskScore = riskScore };
    }

    /// <summary>
    /// <paramref name="verdict"/> unchanged, or degraded to
    /// <see cref="ReviewRequirement.ReviewerConfirmation"/> — keeping its own review window — when
    /// the mandatory-<c>Empty</c> or PV-4 check holds it back. A degrade emits
    /// <c>standing-order.blocked</c> carrying the stable reason code.
    ///
    /// <para>
    /// A no-op for any verdict that is not a <see cref="ReviewRequirement.StandingOrder"/>: both
    /// rules are about approving a write with no person present, and a requirement that already asks
    /// a person has nothing to be held back from.
    /// </para>
    /// </summary>
    /// <param name="verdict">The verdict the policy returned.</param>
    /// <param name="affidavit">The proposal the verdict is about.</param>
    /// <param name="declaredInputs">The provenance sources the policy predicates on (PV-4).</param>
    /// <param name="policyId">The policy's identity on the telemetry event.</param>
    /// <param name="policyVersion">The policy's own version, or null when it does not version itself.</param>
    public static ApprovalVerdict Apply(
        ApprovalVerdict verdict,
        Affidavit affidavit,
        IReadOnlyCollection<ProvenanceSource> declaredInputs,
        string policyId,
        string? policyVersion = null)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(affidavit);
        ArgumentNullException.ThrowIfNull(declaredInputs);
        ArgumentNullException.ThrowIfNull(policyId);

        // Which policy spoke, stamped by the chain rather than reported by the policy: a Standing
        // Order's approval is attributed to it on the Docket row (AZ-1), and a record of who
        // approved a write with no person present has to be the framework's answer.
        verdict = verdict with { PolicyId = policyId, PolicyVersion = policyVersion };

        if (verdict.Requirement != ReviewRequirement.StandingOrder) return verdict;

        // 1. GT-5: a Standing Order never fires over a required field with no known value.
        var empties = StandingOrderGuard.EmptyMandatoryFields(affidavit);
        if (empties.Count > 0)
        {
            var reason = StandingOrderGuard.MandatoryFieldEmptyReason(empties);
            AffiantTelemetry.RecordStandingOrderBlocked(
                policyId,
                blockedReason: StandingOrderBlockedReasons.MandatoryFieldEmpty,
                reason: reason,
                policyVersion: policyVersion,
                // Field NAMES, which are schema. Never a field value: telemetry is operational and
                // the audit record is the Affidavit.
                emptyMandatoryFields: string.Join(", ", empties));

            return verdict.DegradeToReviewer(StandingOrderBlockedReasons.MandatoryFieldEmpty, reason);
        }

        // 2. PV-4: a verdict with no person present never rests on an unbound tag above Conversation.
        var unbound = StandingOrderGuard.FirstUnboundDeclaredInput(affidavit, declaredInputs);
        if (unbound is not null)
        {
            var reason = StandingOrderGuard.UnboundDeclaredInputReason(unbound);
            AffiantTelemetry.RecordStandingOrderBlocked(
                policyId,
                blockedReason: StandingOrderBlockedReasons.UnboundDeclaredInput,
                reason: reason,
                policyVersion: policyVersion,
                provenanceField: unbound.Field,
                provenanceSource: unbound.Source.ToString());

            return verdict.DegradeToReviewer(StandingOrderBlockedReasons.UnboundDeclaredInput, reason);
        }

        return verdict;
    }
}
