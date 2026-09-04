namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Xunit;

public class ApprovalPolicyEvaluatorTests
{
    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class MatchingPolicy(ReviewRequirement requirement) : IApprovalPolicy
    {
        public int CallCount { get; private set; }

        public Task<ApprovalVerdict?> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<ApprovalVerdict?>(requirement);
        }
    }

    private sealed class DeferringPolicy : IApprovalPolicy
    {
        public int CallCount { get; private set; }

        public Task<ApprovalVerdict?> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<ApprovalVerdict?>(null);
        }
    }

    private sealed class TokenCapturingPolicy : IApprovalPolicy
    {
        public CancellationToken CapturedToken { get; private set; }

        public Task<ApprovalVerdict?> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
        {
            CapturedToken = cancellationToken;
            return Task.FromResult<ApprovalVerdict?>(ReviewRequirement.ReviewerConfirmation);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Affidavit MakeAffidavit() => new(
        OperationType: "TestOp",
        EntityType: "TestEntity",
        EntityId: null,
        Fields: [new AffidavitField("field", "value", null,
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "field", 1.0f)))],
        AggregateConfidence: 1.0f,
        PopulatedConfidence: 1.0f,
        EmptyFieldCount: 0,
        Warnings: [],
        RequiresConfirmation: true);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_ZeroPolicies_ReturnsReviewerConfirmation()
    {
        var evaluator = new ApprovalPolicyEvaluator(Array.Empty<IApprovalPolicy>());

        var result = await evaluator.EvaluateAsync(MakeAffidavit(), TestIdentities.Anyone);

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, result!.Requirement);
    }

    [Fact]
    public async Task EvaluateAsync_SingleMatchingPolicy_ReturnsThatRequirement()
    {
        var policy = new MatchingPolicy(ReviewRequirement.StandingOrder);
        var evaluator = new ApprovalPolicyEvaluator([policy]);

        var result = await evaluator.EvaluateAsync(MakeAffidavit(), TestIdentities.Anyone);

        Assert.Equal(ReviewRequirement.StandingOrder, result!.Requirement);
        Assert.Equal(1, policy.CallCount);
    }

    [Fact]
    public async Task EvaluateAsync_SingleDeferringPolicy_ReturnsDefaultFallback()
    {
        var policy = new DeferringPolicy();
        var evaluator = new ApprovalPolicyEvaluator([policy]);

        var result = await evaluator.EvaluateAsync(MakeAffidavit(), TestIdentities.Anyone);

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, result!.Requirement);
        Assert.Equal(1, policy.CallCount);
    }

    [Fact]
    public async Task EvaluateAsync_MultiplePolicies_FirstNonNullWins_SubsequentNotCalled()
    {
        var first = new DeferringPolicy();
        var second = new MatchingPolicy(ReviewRequirement.ReferralRequired);
        var third = new MatchingPolicy(ReviewRequirement.StandingOrder);
        var evaluator = new ApprovalPolicyEvaluator([first, second, third]);

        var result = await evaluator.EvaluateAsync(MakeAffidavit(), TestIdentities.Anyone);

        Assert.Equal(ReviewRequirement.ReferralRequired, result!.Requirement);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
        Assert.Equal(0, third.CallCount);
    }

    [Fact]
    public async Task EvaluateAsync_RespectsDeclaredOrder()
    {
        var standingOrder = new MatchingPolicy(ReviewRequirement.StandingOrder);
        var referral = new MatchingPolicy(ReviewRequirement.ReferralRequired);

        var evaluatorA = new ApprovalPolicyEvaluator([standingOrder, referral]);
        var resultA = await evaluatorA.EvaluateAsync(MakeAffidavit(), TestIdentities.Anyone);

        var evaluatorB = new ApprovalPolicyEvaluator([referral, standingOrder]);
        var resultB = await evaluatorB.EvaluateAsync(MakeAffidavit(), TestIdentities.Anyone);

        Assert.Equal(ReviewRequirement.StandingOrder, resultA!.Requirement);
        Assert.Equal(ReviewRequirement.ReferralRequired, resultB!.Requirement);
    }

    [Fact]
    public async Task EvaluateAsync_PropagatesCancellationToken()
    {
        var policy = new TokenCapturingPolicy();
        var evaluator = new ApprovalPolicyEvaluator([policy]);
        using var cts = new CancellationTokenSource();

        await evaluator.EvaluateAsync(MakeAffidavit(), TestIdentities.Anyone, cts.Token);

        Assert.Equal(cts.Token, policy.CapturedToken);
    }
}
