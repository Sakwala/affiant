namespace Affiant.Policies.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.Policies.Extensions;
using Affiant.Policies.Services;
using Affiant.Policies.StandingOrders;
using Affiant.Policies.Referrals;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Integration tests for the full AddAffiantPolicies → ApprovalPolicyEvaluator pipeline.
/// Verifies policy ordering, first-match semantics, and DI wiring end-to-end.
/// </summary>
public class ApprovalPolicyEvaluatorIntegrationTests
{
    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class FixedScoreCalculator(int score) : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
            => Task.FromResult(score);
    }

    /// <summary>A Standing Order with a Low ceiling: only the lowest risk band auto-approves.</summary>
    private sealed class LowRiskAutoApprovalOrder(RiskScoreCalculatorBase riskScorer)
        : StandingOrderBase(riskScorer)
    {
        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    /// <summary>A Standing Order with no risk ceiling: matching the conditions is the whole test.</summary>
    private sealed class UnconditionalOrder : StandingOrderBase
    {
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
            ProvenanceChain.From(ProvenanceTag.FromInference("field", 1.0f)))],
        AggregateConfidence: 1.0f,
        Warnings: [],
        RequiresConfirmation: false);

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
            new UnconditionalOrder()   // Never reached
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
            new UnconditionalOrder(),
            new AlwaysReferralRule()
        });

        // Order B: Referral first → ReferralRequired wins
        var evaluatorB = new ApprovalPolicyEvaluator(new IApprovalPolicy[]
        {
            new AlwaysReferralRule(),
            new UnconditionalOrder()
        });

        var affidavit = MakeAffidavit();
        var resultA = await evaluatorA.EvaluateAsync(affidavit);
        var resultB = await evaluatorB.EvaluateAsync(affidavit);

        Assert.Equal(ReviewRequirement.StandingOrder, resultA);
        Assert.Equal(ReviewRequirement.ReferralRequired, resultB);
    }

    [Fact]
    public async Task StandingOrder_written_by_the_book_auto_approves_through_the_evaluator()
    {
        // The whole framework contract for an auto-approval rule: subclass StandingOrderBase,
        // implement MatchesAsync. No calculator registered, no threshold declared.
        var services = new ServiceCollection();
        services.AddAffiantPolicies(p => p
            .AddStandingOrder<UnconditionalOrder>()
            .AddDefaultReviewerConfirmation());
        services.AddScoped<ApprovalPolicyEvaluator>();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<ApprovalPolicyEvaluator>();

        var result = await evaluator.EvaluateAsync(MakeAffidavit());

        Assert.Equal(ReviewRequirement.StandingOrder, result);
    }

    [Fact]
    public async Task Risk_score_driven_StandingOrder_auto_approves_low_risk()
    {
        // Ceiling is Low (1); the host's calculator scores this affidavit Low → auto-approves.
        var evaluator = new ApprovalPolicyEvaluator(new IApprovalPolicy[]
        {
            new LowRiskAutoApprovalOrder(new FixedScoreCalculator((int)RiskLevel.Low))
        });

        var result = await evaluator.EvaluateAsync(MakeAffidavit());

        Assert.Equal(ReviewRequirement.StandingOrder, result);
    }

    [Fact]
    public async Task Risk_score_driven_StandingOrder_defers_when_risk_exceeds_the_ceiling()
    {
        // Same order, but the host's calculator scores this affidavit High (3) → no match →
        // falls through to the evaluator's built-in ReviewerConfirmation fallback.
        var evaluator = new ApprovalPolicyEvaluator(new IApprovalPolicy[]
        {
            new LowRiskAutoApprovalOrder(new FixedScoreCalculator((int)RiskLevel.High))
        });

        var result = await evaluator.EvaluateAsync(MakeAffidavit());

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, result);
    }

    [Fact]
    public async Task Risk_score_driven_StandingOrder_resolves_the_host_calculator_from_DI()
    {
        var services = new ServiceCollection();
        services.AddAffiantPolicies(p => p
            .SetRiskScoreCalculator<AlwaysLowCalculator>()
            .AddStandingOrder<LowRiskAutoApprovalOrder>()
            .AddDefaultReviewerConfirmation());
        services.AddScoped<ApprovalPolicyEvaluator>();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<ApprovalPolicyEvaluator>();

        var result = await evaluator.EvaluateAsync(MakeAffidavit());

        Assert.Equal(ReviewRequirement.StandingOrder, result);
    }

    private sealed class AlwaysLowCalculator : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
            => Task.FromResult((int)RiskLevel.Low);
    }
}
