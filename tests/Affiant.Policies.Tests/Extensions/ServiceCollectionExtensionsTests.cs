namespace Affiant.Policies.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Policies.Extensions;
using Affiant.Policies.Referrals;
using Affiant.Policies.Services;
using Affiant.Policies.StandingOrders;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class ServiceCollectionExtensionsTests
{
    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class TestStandingOrder : StandingOrderBase
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class ThresholdStandingOrder(RiskScoreCalculatorBase? riskScorer = null)
        : StandingOrderBase(riskScorer)
    {
        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class TestReferralRule : ReferralRuleBase
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);

        protected override Task<string?> GetReferredToUserIdAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult<string?>("manager-1");
    }

    private sealed class CustomRiskCalculator : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
            => Task.FromResult(1); // always Low
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Affidavit EmptyAffidavit() => Affidavit.Create(
        operationType: "Test",
        entityType: "TestEntity",
        entityId: null,
        fields: [],
        warnings: [],
        requiresConfirmation: false);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAffiantPolicies_registers_no_scoring_formula()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies();
        var sp = services.BuildServiceProvider();

        // A placeholder is registered so an order that takes the calculator as a required
        // constructor dependency still resolves — but it carries no formula and no risk floor:
        // asking it to score anything throws, naming the registration that is missing.
        var placeholder = sp.GetRequiredService<RiskScoreCalculatorBase>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => placeholder.ComputeAsync(EmptyAffidavit()));

        Assert.Contains("SetRiskScoreCalculator<T>()", ex.Message);
    }

    [Fact]
    public void AddStandingOrder_registers_policy_as_IApprovalPolicy()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies(p => p.AddStandingOrder<TestStandingOrder>());
        var sp = services.BuildServiceProvider();

        var policies = sp.GetServices<IApprovalPolicy>().ToList();
        Assert.Single(policies);
        Assert.IsType<TestStandingOrder>(policies[0]);
    }

    [Fact]
    public async Task StandingOrder_with_a_threshold_and_no_calculator_fails_on_first_evaluation()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies(p => p.AddStandingOrder<ThresholdStandingOrder>());
        var sp = services.BuildServiceProvider();

        // Resolution succeeds — the failure belongs to the policy, not to the container.
        var policy = Assert.Single(sp.GetServices<IApprovalPolicy>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone));

        Assert.Contains(nameof(ThresholdStandingOrder), ex.Message);
        Assert.Contains("SetRiskScoreCalculator<T>()", ex.Message);
    }

    [Fact]
    public void SetRiskScoreCalculator_wins_when_called_after_AddStandingOrder()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies(p => p
            .AddStandingOrder<ThresholdStandingOrder>()
            .SetRiskScoreCalculator<CustomRiskCalculator>());
        var sp = services.BuildServiceProvider();

        Assert.IsType<CustomRiskCalculator>(sp.GetRequiredService<RiskScoreCalculatorBase>());
    }

    [Fact]
    public void SetRiskScoreCalculator_wins_when_called_before_AddStandingOrder()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies(p => p
            .SetRiskScoreCalculator<CustomRiskCalculator>()
            .AddStandingOrder<ThresholdStandingOrder>());
        var sp = services.BuildServiceProvider();

        Assert.IsType<CustomRiskCalculator>(sp.GetRequiredService<RiskScoreCalculatorBase>());
    }

    [Fact]
    public async Task Registration_order_does_not_change_how_a_threshold_order_evaluates()
    {
        // The same order, the same calculator, the two possible call orders: identical outcome.
        static IApprovalPolicy Resolve(Action<PoliciesBuilder> configure)
        {
            var services = new ServiceCollection();
            services.AddAffiantPolicies(configure);
            return Assert.Single(services.BuildServiceProvider().GetServices<IApprovalPolicy>());
        }

        var scorerFirst = Resolve(p => p
            .SetRiskScoreCalculator<CustomRiskCalculator>()
            .AddStandingOrder<ThresholdStandingOrder>());

        var orderFirst = Resolve(p => p
            .AddStandingOrder<ThresholdStandingOrder>()
            .SetRiskScoreCalculator<CustomRiskCalculator>());

        Assert.Equal(ReviewRequirement.StandingOrder, (await scorerFirst.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone))!.Requirement);
        Assert.Equal(ReviewRequirement.StandingOrder, (await orderFirst.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone))!.Requirement);
    }

    [Fact]
    public void StandingOrder_with_a_threshold_resolves_once_a_calculator_is_registered()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies(p => p
            .SetRiskScoreCalculator<CustomRiskCalculator>()
            .AddStandingOrder<ThresholdStandingOrder>());
        var sp = services.BuildServiceProvider();

        var policies = sp.GetServices<IApprovalPolicy>().ToList();

        Assert.Single(policies);
        Assert.IsType<ThresholdStandingOrder>(policies[0]);
    }

    [Fact]
    public void AddReferralRule_registers_policy_as_IApprovalPolicy()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies(p => p.AddReferralRule<TestReferralRule>());
        var sp = services.BuildServiceProvider();

        var policies = sp.GetServices<IApprovalPolicy>().ToList();
        Assert.Single(policies);
        Assert.IsType<TestReferralRule>(policies[0]);
    }

    [Fact]
    public void AddDefaultReviewerConfirmation_registers_fallback_policy()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies(p => p.AddDefaultReviewerConfirmation());
        var sp = services.BuildServiceProvider();

        var policies = sp.GetServices<IApprovalPolicy>().ToList();
        Assert.Single(policies);
        // Type is internal; verify it returns ReviewerConfirmation
    }

    [Fact]
    public void Policy_registration_order_is_preserved()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies(p => p
            .AddStandingOrder<TestStandingOrder>()
            .AddReferralRule<TestReferralRule>()
            .AddDefaultReviewerConfirmation());

        var sp = services.BuildServiceProvider();
        var policies = sp.GetServices<IApprovalPolicy>().ToList();

        Assert.Equal(3, policies.Count);
        Assert.IsType<TestStandingOrder>(policies[0]);
        Assert.IsType<TestReferralRule>(policies[1]);
        // policies[2] is the internal DefaultReviewerConfirmationPolicy
    }

    [Fact]
    public void SetRiskScoreCalculator_registers_the_host_calculator()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies(p => p.SetRiskScoreCalculator<CustomRiskCalculator>());
        var sp = services.BuildServiceProvider();

        var calculator = sp.GetRequiredService<RiskScoreCalculatorBase>();
        Assert.IsType<CustomRiskCalculator>(calculator);
    }

    [Fact]
    public void AddAffiantPolicies_leaves_a_host_registered_calculator_alone()
    {
        var services = new ServiceCollection();

        // Host registers their calculator directly rather than through the builder.
        services.AddScoped<RiskScoreCalculatorBase, CustomRiskCalculator>();
        services.AddAffiantPolicies();

        var sp = services.BuildServiceProvider();
        var calculator = sp.GetRequiredService<RiskScoreCalculatorBase>();
        Assert.IsType<CustomRiskCalculator>(calculator);
    }
}
