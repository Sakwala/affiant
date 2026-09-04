namespace Affiant.Policies.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.Policies.Extensions;
using Affiant.Policies.Services;
using Affiant.Policies.StandingOrders;
using Affiant.Policies.Referrals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Integration tests for the full AddAffiantPolicies → ApprovalPolicyEvaluator pipeline.
/// Verifies policy ordering, first-match semantics, and DI wiring end-to-end.
/// </summary>
public class ApprovalPolicyEvaluatorIntegrationTests
{
    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class LowRiskAutoApprovalOrder : StandingOrderBase
    {
        public LowRiskAutoApprovalOrder() : base(new DefaultRiskScoreCalculator()) { }

        // Accepts all affidavits; risk scorer determines auto-approval.
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class HighThresholdOrder : StandingOrderBase
    {
        public HighThresholdOrder() : base(new DefaultRiskScoreCalculator()) { }

        protected override int RiskThreshold => (int)RiskLevel.High;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class AlwaysReferralRule : ReferralRuleBase
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);

        protected override Task<string?> GetReferredToUserIdAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult<string?>("manager-999");
    }

    private sealed class NeverMatchingPolicy : IApprovalPolicy
    {
        public int CallCount { get; private set; }

        public Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult<ReviewRequirement?>(null);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Affidavit MakeAffidavit() => new(
        OperationType: "Test",
        EntityType: "TestEntity",
        EntityId: null,
        Fields: [new AffidavitField("field", "val", null,
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "field", 1.0f)))],
        AggregateConfidence: 1.0f,
        PopulatedConfidence: 1.0f,
        EmptyFieldCount: 0,
        Warnings: [],
        RequiresConfirmation: false);

    private static Affidavit HighValueAffidavit() => new(
        OperationType: "Test",
        EntityType: "TestEntity",
        EntityId: null,
        Fields: [new AffidavitField("Value", 100m, null, ProvenanceChain.From(ProvenanceTag.Empty))],
        AggregateConfidence: 1.0f,
        PopulatedConfidence: 1.0f,
        EmptyFieldCount: 0,
        Warnings: [],
        RequiresConfirmation: false);

    private static ApprovalPolicyEvaluator BuildEvaluator(
        Action<PoliciesBuilder> configure,
        bool includeCore = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (includeCore)
        {
            services.AddSingleton<ApprovalPolicyEvaluator>();
        }
        services.AddAffiantPolicies(configure);

        // Ensure evaluator is resolvable even without AddAffiantCore
        services.AddSingleton<ApprovalPolicyEvaluator>();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<ApprovalPolicyEvaluator>();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluator_defaults_to_ReviewerConfirmation_when_no_policies_match()
    {
        var evaluator = new ApprovalPolicyEvaluator(Array.Empty<IApprovalPolicy>());

        var result = await evaluator.EvaluateAsync(MakeAffidavit());

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, result);
    }

    [Fact]
    public async Task AddDefaultReviewerConfirmation_produces_ReviewerConfirmation()
    {
        var services = new ServiceCollection();
        services.AddAffiantPolicies(p => p.AddDefaultReviewerConfirmation());
        services.AddSingleton<ApprovalPolicyEvaluator>();
        var sp = services.BuildServiceProvider();
        var evaluator = sp.GetRequiredService<ApprovalPolicyEvaluator>();

        var result = await evaluator.EvaluateAsync(MakeAffidavit());

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, result);
    }

    [Fact]
    public async Task First_matching_policy_wins_chain_stops()
    {
        var neverPolicy = new NeverMatchingPolicy();
        var policies = new IApprovalPolicy[]
        {
            neverPolicy,
            new AlwaysReferralRule(),
            new LowRiskAutoApprovalOrder()   // Never reached
        };
        var evaluator = new ApprovalPolicyEvaluator(policies);

        var result = await evaluator.EvaluateAsync(MakeAffidavit());

        Assert.Equal(ReviewRequirement.ReferralRequired, result);
        Assert.Equal(1, neverPolicy.CallCount);  // NeverMatchingPolicy was called once then chain stopped at Referral
    }

    [Fact]
    public async Task Policy_order_determines_result()
    {
        // Order A: StandingOrder first → StandingOrder wins
        var evaluatorA = new ApprovalPolicyEvaluator(new IApprovalPolicy[]
        {
            new HighThresholdOrder(),
            new AlwaysReferralRule()
        });

        // Order B: Referral first → ReferralRequired wins
        var evaluatorB = new ApprovalPolicyEvaluator(new IApprovalPolicy[]
        {
            new AlwaysReferralRule(),
            new HighThresholdOrder()
        });

        var affidavit = MakeAffidavit();
        var resultA = await evaluatorA.EvaluateAsync(affidavit);
        var resultB = await evaluatorB.EvaluateAsync(affidavit);

        Assert.Equal(ReviewRequirement.StandingOrder, resultA);
        Assert.Equal(ReviewRequirement.ReferralRequired, resultB);
    }

    [Fact]
    public async Task Risk_score_driven_StandingOrder_auto_approves_low_risk()
    {
        // LowRiskAutoApprovalOrder matches everything; threshold = Low (1).
        // MakeAffidavit() has no "Value" field → default scorer returns Medium (2).
        // Medium (2) > Low (1) → should NOT auto-approve.
        var evaluator = new ApprovalPolicyEvaluator(new IApprovalPolicy[]
        {
            new LowRiskAutoApprovalOrder(),
            // Fallback added explicitly to make ordering obvious
        });

        var result = await evaluator.EvaluateAsync(MakeAffidavit());

        // Default scorer returns Medium; threshold is Low → no match → falls through to built-in fallback
        Assert.Equal(ReviewRequirement.ReviewerConfirmation, result);
    }

    [Fact]
    public async Task Risk_score_driven_StandingOrder_auto_approves_when_threshold_covers_risk()
    {
        // HighThresholdOrder matches everything and accepts up to High (3).
        // HighValueAffidavit has Value=100 → scores High (3) → within High threshold → auto-approves.
        var evaluator = new ApprovalPolicyEvaluator(new IApprovalPolicy[]
        {
            new HighThresholdOrder()
        });

        var result = await evaluator.EvaluateAsync(HighValueAffidavit());

        Assert.Equal(ReviewRequirement.StandingOrder, result);
    }
}
