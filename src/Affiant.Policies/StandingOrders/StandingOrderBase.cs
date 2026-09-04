using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Affiant.Policies.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Affiant.Policies.StandingOrders;

/// <summary>
/// Base class for Standing Order policies that auto-approve operations without human review.
/// A Standing Order auto-approves when the Affidavit matches the policy's conditions and,
/// if the policy declares a <see cref="RiskThreshold"/>, the host-computed risk score is at or
/// below it.
///
/// Subclass and implement <see cref="MatchesAsync"/> to describe when the order applies — that
/// alone is a complete, working Standing Order. Override <see cref="RiskThreshold"/> only to
/// add a risk ceiling on top of the match, which requires a <see cref="RiskScoreCalculatorBase"/>
/// registered with <c>SetRiskScoreCalculator&lt;T&gt;()</c>; a policy that declares a ceiling
/// without one fails on its first evaluation, before any write is auto-approved, rather than
/// approving or refusing on an unscored guess.
/// Override <see cref="GetAutoApproverIdAsync"/> to record a named approver in logs.
/// </summary>
public abstract class StandingOrderBase : IApprovalPolicy
{
    protected readonly ILogger Logger;

    /// <summary>
    /// The host's risk calculator, or null when this Standing Order declares no
    /// <see cref="RiskThreshold"/> and therefore needs no score.
    /// </summary>
    protected readonly RiskScoreCalculatorBase? RiskScorer;

    /// <summary>
    /// Risk score at or below which the Standing Order auto-approves, or null — the default —
    /// for no risk ceiling at all: matching the conditions is the whole test.
    /// </summary>
    protected virtual int? RiskThreshold => null;

    protected StandingOrderBase(RiskScoreCalculatorBase? riskScorer = null, ILogger? logger = null)
    {
        RiskScorer = riskScorer;
        Logger = logger ?? NullLogger.Instance;

        // Deliberately no risk-configuration check here. RiskThreshold is virtual, so reading it
        // from this constructor dispatches to an override whose own fields are not yet assigned —
        // an override that reads injected configuration (`=> _config.Ceiling`) would throw a
        // NullReferenceException during DI resolution. The check lives at the top of
        // EvaluateAsync instead, where the object is fully built, and is available eagerly to a
        // host that wants it via AffiantPolicies.ValidateStandingOrders(IServiceProvider).
    }

    /// <summary>
    /// Returns true if this Standing Order's conditions match the given Affidavit.
    /// </summary>
    protected abstract Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the ID of the user recorded as auto-approver, or null for system approval.
    /// Default: null.
    /// </summary>
    protected virtual Task<string?> GetAutoApproverIdAsync(Affidavit affidavit, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Throws when this Standing Order declares a <see cref="RiskThreshold"/> but has no usable
    /// <see cref="RiskScoreCalculatorBase"/> — none injected, or only the placeholder
    /// <c>AddAffiantPolicies</c> registers when the host supplied no calculator.
    /// Returns the declared ceiling and the calculator that will score it; both null when the
    /// order declares no ceiling and so needs no calculator at all.
    /// </summary>
    internal (int? Threshold, RiskScoreCalculatorBase? Scorer) EnsureConfigured()
    {
        var threshold = RiskThreshold;
        if (threshold is null)
            return (null, null);

        var scorer = RiskScorer;
        if (scorer is null or MissingRiskScoreCalculator)
            throw new InvalidOperationException(MissingRiskScoreCalculator.MessageFor(GetType()));

        return (threshold, scorer);
    }

    /// <summary>
    /// The provenance sources this Standing Order predicates on (protocol rule PV-4). Empty by
    /// default: a Standing Order whose <see cref="MatchesAsync"/> looks only at field values or host
    /// state predicates on nothing outside the conversation, and the rule leaves it alone. Override
    /// it — <c>=&gt; [ProvenanceSource.External]</c> — when the match reads a grade a caller could
    /// have asserted with nothing behind it, and the check below will require every such tag in
    /// force to point at something an auditor can re-fetch before this order fires.
    /// </summary>
    protected virtual IReadOnlyCollection<ProvenanceSource> DeclaredInputs => [];

    /// <summary>
    /// This order's own review window, used when a person ends up being asked anyway — because one
    /// of the three checks below held the order back — and the verdict names none (protocol rule
    /// GT-4). Null by default, so the gate's own default applies. The degrade changes who decides,
    /// not when the window closes, which is why the window is the order's to name even on a verdict
    /// it did not get to keep.
    /// </summary>
    protected virtual TimeSpan? StandingOrderTimeToLive => null;

    /// <inheritdoc />
    IReadOnlyCollection<ProvenanceSource> IApprovalPolicy.DeclaredInputs => DeclaredInputs;

    /// <inheritdoc />
    TimeSpan? IApprovalPolicy.DefaultTimeToLive => StandingOrderTimeToLive;

    /// <summary>
    /// The order's verdict, with the three checks that hold a person-free approval back applied in
    /// the order protocol rule GT-5 fixes:
    ///
    /// <list type="number">
    /// <item><description><b>The empty required field.</b> No proposed field marked mandatory may
    /// read <c>Empty</c>. First because it is the cheapest read and the least conditional — it
    /// depends on nothing this policy declared and nothing a host port returns, so a proposal with a
    /// hole in it is held back identically under every wiring, and a host's risk scorer is never
    /// spent on it.</description></item>
    /// <item><description><b>The unbound declared input (PV-4).</b> Every field whose tag in force
    /// names one of <see cref="DeclaredInputs"/> and sits above
    /// <see cref="ProvenanceSource.Conversation"/> must point at something an auditor can re-check.
    /// Still a pure read of the Affidavit, and still cheaper than a score.</description></item>
    /// <item><description><b>The risk comparison.</b> An order that declares no
    /// <see cref="RiskThreshold"/> fires on the match alone and needs no calculator. One that
    /// declares a ceiling fires only when the host's score is at or below it. The framework owns the
    /// comparison; the host owns the number.</description></item>
    /// </list>
    ///
    /// <para>
    /// A check that fires <b>degrades</b> the verdict to
    /// <see cref="ReviewRequirement.ReviewerConfirmation"/> rather than returning <c>null</c>: the
    /// order matched and had an opinion, and the record has to say a Standing Order was held back
    /// rather than let a later policy speak as though this one never fired. Degrading toward a
    /// person is always safe. The order's own review window survives the degrade.
    /// </para>
    /// </summary>
    public async Task<ApprovalVerdict?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        // Configuration first, before the conditions are even tested: a Standing Order that
        // declares a risk ceiling with no calculator to score it fails here — on its first
        // evaluation, before any write is auto-approved, never silently.
        var (threshold, scorer) = EnsureConfigured();

        if (!await MatchesAsync(affidavit, cancellationToken).ConfigureAwait(false))
            return null;

        var verdict = new ApprovalVerdict(
            ReviewRequirement.StandingOrder,
            TimeToLive: StandingOrderTimeToLive);

        // Checks 1 and 2 — both pure reads of the Affidavit, both before the scorer is spent.
        var guarded = StandingOrderGuardrails.Apply(
            verdict, affidavit, DeclaredInputs, PolicyId, PolicyVersion);
        if (guarded.Requirement != ReviewRequirement.StandingOrder)
        {
            Logger.LogInformation(
                "Standing Order {Policy} was not honoured ({BlockedReason}): {Reason}",
                GetType().Name, guarded.BlockedReason, guarded.Reason);
            return guarded;
        }

        // EnsureConfigured returns a scorer whenever it returns a threshold, and throws otherwise,
        // so a null scorer here can only mean this order declares no ceiling.
        if (threshold is null || scorer is null)
        {
            var matchApproverId = await GetAutoApproverIdAsync(affidavit, cancellationToken).ConfigureAwait(false);
            Logger.LogInformation(
                "Standing Order {Policy} auto-approved: conditions matched, no risk ceiling declared, approver {Approver}",
                GetType().Name, matchApproverId ?? "[system]");

            // TL-1 `standing-order.fired` (AZ-1). No `risk.score` attribute: nothing was scored, and
            // an absent attribute is honest where a zero would read as "scored, and it was zero".
            AffiantTelemetry.RecordStandingOrderFired(PolicyId, policyVersion: PolicyVersion);

            return verdict;
        }

        // Check 3 — the risk comparison. The framework ships no scoring formula and no floor.
        var riskScore = await scorer.ComputeAsync(affidavit, cancellationToken).ConfigureAwait(false);

        if (riskScore <= threshold.Value)
        {
            var approverId = await GetAutoApproverIdAsync(affidavit, cancellationToken).ConfigureAwait(false);
            Logger.LogInformation(
                "Standing Order {Policy} auto-approved: risk {Score} \u2264 threshold {Threshold}, approver {Approver}",
                GetType().Name, riskScore, threshold.Value, approverId ?? "[system]");

            // TL-1 `standing-order.fired` (AZ-1): a write was approved with no person present, which
            // is the single most consequential thing a policy can do and the one an operator most
            // needs to be able to count. `entry.id` is absent because IApprovalPolicy.EvaluateAsync
            // is handed the Affidavit alone — the gate has not filed an entry when the chain runs.
            AffiantTelemetry.RecordStandingOrderFired(PolicyId, riskScore, policyVersion: PolicyVersion);

            return verdict;
        }

        var overThreshold =
            $"GT-5: the host's risk score ({riskScore}) is above this Standing Order's threshold " +
            $"({threshold.Value}), so a person still confirms this write.";

        Logger.LogInformation(
            "Standing Order {Policy} matched conditions but risk {Score} exceeds threshold {Threshold}",
            GetType().Name, riskScore, threshold.Value);

        // TL-1 `standing-order.blocked` (GT-5). The stable code is a separate attribute from the
        // sentence: a dashboard alerts on the code, and the sentence stays free to be rewritten for
        // whoever reads the card.
        AffiantTelemetry.RecordStandingOrderBlocked(
            PolicyId,
            blockedReason: StandingOrderBlockedReasons.RiskAboveThreshold,
            reason: overThreshold,
            riskScore: riskScore,
            riskThreshold: threshold.Value,
            policyVersion: PolicyVersion);

        return verdict.DegradeToReviewer(StandingOrderBlockedReasons.RiskAboveThreshold, overThreshold);
    }

    /// <summary>
    /// The policy's identity in telemetry (<c>policy.id</c>). Defaults to the concrete policy type's
    /// full name, which is stable across releases in a way a display name is not. Override it when
    /// a host names its policies in configuration and wants alerts keyed on that name instead.
    /// </summary>
    protected virtual string PolicyId => GetType().FullName ?? GetType().Name;

    /// <summary>
    /// The policy's own version in telemetry (<c>policy.version</c>), or <see langword="null"/> when
    /// the policy does not version itself. Override it when a host revises a policy's rules and
    /// needs to tell an approval made under the old rules from one made under the new.
    /// </summary>
    protected virtual string? PolicyVersion => null;
}
