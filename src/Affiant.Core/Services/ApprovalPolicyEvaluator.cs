namespace Affiant.Core.Services;

using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;

/// <summary>
/// Walks the registered <see cref="IApprovalPolicy"/> chain in registration order and returns the
/// first non-null verdict — with the GT-5 and PV-4 checks applied to it, its review window resolved,
/// and the two policy faults no wire-up check can see refused rather than swallowed.
///
/// <para>
/// <b>The chain's own default is a person</b> (protocol rule GT-1). A chain that produces no verdict
/// returns <see cref="ReviewRequirement.ReviewerConfirmation"/>, so a gate with no policies at all
/// asks a person about everything. That is the fail-closed direction and it is the one place the
/// framework has an opinion about approval at all.
/// </para>
///
/// <para>
/// <b>Two faults are refused here, with nothing filed</b> (protocol rule CV-1). A verdict carrying a
/// review window that is not a deadline, and a policy whose <c>EvaluateAsync</c> throws, are both a
/// policy breaking its own contract in a way no startup check could have seen. Both raise
/// <see cref="AffiantPolicyException"/> carrying <c>wireup-invalid</c> after emitting
/// <c>policy.invalid</c>. Neither is swallowed: a chain that cannot answer must not fall through to
/// a weaker requirement, and the gate's caller — the tool seam — turns the refusal into the error
/// arm of the tool result so the model is told the truth rather than seeing a raw stack trace or,
/// worse, an unreviewed write reported as done.
/// </para>
/// </summary>
public sealed class ApprovalPolicyEvaluator(IEnumerable<IApprovalPolicy> policies) : IApprovalPolicyEvaluator
{
    /// <inheritdoc />
    public async Task<ApprovalVerdict> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affidavit);
        ArgumentNullException.ThrowIfNull(identity);

        foreach (var policy in policies)
        {
            var verdict = await EvaluateOneAsync(policy, affidavit, identity, cancellationToken)
                .ConfigureAwait(false);
            if (verdict is null) continue;

            CheckTimeToLive(policy, nameof(ApprovalVerdict.TimeToLive), verdict.TimeToLive);

            // GT-4: the fallback is read here, so it is checked here too — a policy whose verdict
            // names no window and whose own default is unusable must not reach the filing step with
            // a number that cannot be stamped.
            if (verdict.TimeToLive is null)
                CheckTimeToLive(policy, nameof(IApprovalPolicy.DefaultTimeToLive), policy.DefaultTimeToLive);

            var resolved = verdict.TimeToLive is null
                ? verdict with { TimeToLive = policy.DefaultTimeToLive }
                : verdict;

            // GT-5 and PV-4. A verdict from StandingOrderBase has already been through these, so
            // this pass changes nothing for it; a verdict from a policy written against the bare
            // interface has not, and the rule is that the gate checks before honouring one.
            //
            // The chain stamps the policy that produced the verdict, rather than trusting a policy
            // to name itself: a Standing Order's attestation names it (AZ-1), and a record of who
            // approved a write with no person present has to be the framework's answer, not the
            // approver's own.
            return StandingOrderGuardrails.Apply(
                resolved,
                affidavit,
                policy.DeclaredInputs,
                PolicyIdOf(policy),
                policy.PolicyVersion);
        }

        // The chain's own fallback: a person. No policy produced it, so it names none.
        return new ApprovalVerdict(ReviewRequirement.ReviewerConfirmation);
    }

    /// <summary>
    /// Ask one policy, turning a throw out of its <c>EvaluateAsync</c> into a stated refusal.
    ///
    /// <para>
    /// A host policy that throws is a host bug, but it reaches the gate through the tool seam, and
    /// an unhandled <c>NullReferenceException</c> out of a gated tool call tells a host nothing it
    /// can branch on and tells the model nothing at all. So it becomes an
    /// <see cref="AffiantPolicyException"/> carrying <c>wireup-invalid</c> — the same code every
    /// other "this gate is wired wrong" refusal carries — with the original throw kept as the inner
    /// exception so the bug stays findable. An <see cref="AffiantRefusalException"/> the policy
    /// raised itself passes through untouched, so a policy that deliberately refuses keeps its own
    /// code, and cancellation is not a fault.
    /// </para>
    /// </summary>
    private async Task<ApprovalVerdict?> EvaluateOneAsync(
        IApprovalPolicy policy,
        Affidavit affidavit,
        ConversationIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            return await policy.EvaluateAsync(affidavit, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AffiantRefusalException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var policyId = PolicyIdOf(policy);
            var reason =
                $"CV-1: approval policy '{policyId}' threw from EvaluateAsync — " +
                $"{ex.GetType().Name}: {ex.Message}. A policy that cannot answer is a wiring the " +
                "gate cannot run: nothing was filed, and the call is refused rather than the throw " +
                "escaping through the tool seam.";

            // TL-1 `policy.invalid` (CV-1). Emitted before the throw so an operator can see WHICH
            // policy is throwing without reading a stack trace out of an aggregated log, and so a
            // rising rate is alertable.
            AffiantTelemetry.RecordPolicyInvalid(policyId, option: "evaluate", reason: reason);
            throw new AffiantPolicyException(reason, ex);
        }
    }

    /// <summary>
    /// Refuse a review window that is not a deadline (GT-4), from a verdict or from the policy's own
    /// declared default. <see langword="null"/> is not a fault — it means "I have nothing to say
    /// about the deadline" and falls through to the next source.
    /// </summary>
    private static void CheckTimeToLive(IApprovalPolicy policy, string option, TimeSpan? timeToLive)
    {
        if (timeToLive is not { } ttl) return;
        if (ReviewDeadline.IsUsable(ttl, DateTimeOffset.UtcNow, out var why)) return;

        var policyId = PolicyIdOf(policy);
        var reason = ReviewDeadline.UnusableMessage(policyId, option, ttl, why!);
        AffiantTelemetry.RecordPolicyInvalid(policyId, option: option, reason: reason);
        throw new AffiantPolicyException(reason);
    }

    private static string PolicyIdOf(IApprovalPolicy policy) => policy.PolicyId;
}
