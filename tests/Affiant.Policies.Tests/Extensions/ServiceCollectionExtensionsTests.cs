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
        public TestStandingOrder() : base(new DefaultRiskScoreCalculator()) { }

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

    private sealed class CustomRiskCalculator : RiskScoreCalculator
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
            => Task.FromResult(1); // always Low
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AddAffiantPolicies_registers_default_risk_score_calculator()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies();
        var sp = services.BuildServiceProvider();

        var calculator = sp.GetService<RiskScoreCalculator>();
        Assert.NotNull(calculator);
        Assert.IsType<DefaultRiskScoreCalculator>(calculator);
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
    public void SetRiskScoreCalculator_replaces_default_calculator()
    {
        var services = new ServiceCollection();

        services.AddAffiantPolicies(p => p.SetRiskScoreCalculator<CustomRiskCalculator>());
        var sp = services.BuildServiceProvider();

        var calculator = sp.GetRequiredService<RiskScoreCalculator>();
        Assert.IsType<CustomRiskCalculator>(calculator);
    }

    [Fact]
    public void AddAffiantPolicies_does_not_replace_host_registered_calculator()
    {
        var services = new ServiceCollection();

        // Host registers their calculator first.
        services.AddScoped<RiskScoreCalculator, CustomRiskCalculator>();
        // AddAffiantPolicies should not overwrite it (TryAdd semantics).
        services.AddAffiantPolicies();

        var sp = services.BuildServiceProvider();
        var calculator = sp.GetRequiredService<RiskScoreCalculator>();
        Assert.IsType<CustomRiskCalculator>(calculator);
    }
}
