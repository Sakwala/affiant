namespace Affiant.Policies.Tests.StandingOrders;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Policies;
using Affiant.Policies.Extensions;
using Affiant.Policies.Services;
using Affiant.Policies.StandingOrders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The host shapes a Standing Order can actually take, resolved through DI: a ceiling read from
/// injected configuration, a constructor written against <c>1.0.0-beta.1</c> that takes the
/// calculator as a required dependency, and the eager
/// <see cref="AffiantPolicies.ValidateStandingOrders"/> check.
/// </summary>
public class RiskConfigurationTests
{
    // ── Host doubles ──────────────────────────────────────────────────────────

    /// <summary>The host's own configuration object, injected into the order.</summary>
    private sealed class ThresholdConfig
    {
        public int Ceiling { get; init; } = (int)RiskLevel.Low;
    }

    private sealed class FixedScoreCalculator(int score) : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
            => Task.FromResult(score);
    }

    private sealed class AlwaysLowCalculator : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
            => Task.FromResult((int)RiskLevel.Low);
    }

    private sealed class AlwaysHighCalculator : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
            => Task.FromResult((int)RiskLevel.High);
    }

    /// <summary>
    /// The ceiling is read off injected configuration, so <c>RiskThreshold</c> dereferences a
    /// field this class's constructor assigns — after <c>StandingOrderBase</c>'s has run.
    /// </summary>
    private sealed class ConfigDrivenOrder : StandingOrderBase
    {
        private readonly ThresholdConfig _config;

        // Assigned in the constructor *body*, i.e. after base(...) has returned. A base
        // constructor that read the virtual RiskThreshold would dereference a null _config.
        public ConfigDrivenOrder(ThresholdConfig config, RiskScoreCalculatorBase? riskScorer = null)
            : base(riskScorer)
        {
            _config = config;
        }

        protected override int? RiskThreshold => _config.Ceiling;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    /// <summary>
    /// A Standing Order written against <c>1.0.0-beta.1</c>, whose base constructor took the
    /// calculator as a required parameter — so this one does too, with no default.
    /// </summary>
    private sealed class Beta1ShapeOrder(RiskScoreCalculatorBase riskScorer, ILogger<Beta1ShapeOrder> logger)
        : StandingOrderBase(riskScorer, logger)
    {
        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    /// <summary>The same beta.1 constructor shape, but with no ceiling declared.</summary>
    private sealed class Beta1ShapeCeilinglessOrder(RiskScoreCalculatorBase riskScorer)
        : StandingOrderBase(riskScorer)
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    /// <summary>A Standing Order by the book: match is the whole test, no calculator needed.</summary>
    private sealed class ByTheBookOrder : StandingOrderBase
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Affidavit EmptyAffidavit() => Affidavit.Create(
        operationType: "Test",
        entityType: "TestEntity",
        entityId: null,
        fields: [],
        warnings: [],
        requiresConfirmation: false);

    private static ServiceCollection HostServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        return services;
    }

    private sealed class CountingCalculator : RiskScoreCalculatorBase
    {
        internal static int Calls;

        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult((int)RiskLevel.Low);
        }
    }

    // ── A ceiling read from injected configuration ────────────────────────────

    [Fact]
    public async Task Config_driven_ceiling_resolves_from_DI_and_evaluates()
    {
        var services = HostServices();
        services.AddSingleton(new ThresholdConfig { Ceiling = (int)RiskLevel.Low });
        services.AddAffiantPolicies(p => p
            .SetRiskScoreCalculator<AlwaysLowCalculator>()
            .AddStandingOrder<ConfigDrivenOrder>());

        using var scope = services.BuildServiceProvider().CreateScope();

        // Resolution must not throw: RiskThreshold dereferences _config, which is unassigned
        // while the base constructor runs.
        var policy = Assert.Single(scope.ServiceProvider.GetServices<IApprovalPolicy>());

        Assert.Equal(ReviewRequirement.StandingOrder, (await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone))!.Requirement);
    }

    [Fact]
    public async Task Config_driven_ceiling_refuses_a_score_above_it()
    {
        var services = HostServices();
        services.AddSingleton(new ThresholdConfig { Ceiling = (int)RiskLevel.Low });
        services.AddAffiantPolicies(p => p
            .SetRiskScoreCalculator<AlwaysHighCalculator>()
            .AddStandingOrder<ConfigDrivenOrder>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var policy = Assert.Single(scope.ServiceProvider.GetServices<IApprovalPolicy>());

        // Held back by the ceiling degrades to a person; it does not vanish (GT-5).
        var verdict = await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone);
        Assert.Equal(ReviewRequirement.ReviewerConfirmation, verdict!.Requirement);
        Assert.Equal(StandingOrderBlockedReasons.RiskAboveThreshold, verdict.BlockedReason);
    }

    [Fact]
    public async Task Config_driven_ceiling_with_no_calculator_names_the_fix()
    {
        var services = HostServices();
        services.AddSingleton(new ThresholdConfig());
        services.AddAffiantPolicies(p => p.AddStandingOrder<ConfigDrivenOrder>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var policy = Assert.Single(scope.ServiceProvider.GetServices<IApprovalPolicy>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone));

        Assert.Contains(nameof(ConfigDrivenOrder), ex.Message);
        Assert.Contains("SetRiskScoreCalculator<T>()", ex.Message);
    }

    // ── An order whose constructor still requires the calculator ──────────────

    [Fact]
    public async Task A_beta1_shaped_order_resolves_and_names_the_fix()
    {
        var services = HostServices();
        services.AddAffiantPolicies(p => p.AddStandingOrder<Beta1ShapeOrder>());

        using var scope = services.BuildServiceProvider().CreateScope();

        // The placeholder satisfies the required constructor parameter, so the container does not
        // refuse the order with its own "Unable to resolve service for type ..." message.
        var policy = Assert.Single(scope.ServiceProvider.GetServices<IApprovalPolicy>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone));

        Assert.Contains(nameof(Beta1ShapeOrder), ex.Message);
        Assert.Contains("SetRiskScoreCalculator<T>()", ex.Message);
        Assert.DoesNotContain("Unable to resolve service", ex.Message);
    }

    [Fact]
    public async Task A_beta1_shaped_order_fires_once_the_host_registers_a_calculator()
    {
        var services = HostServices();
        services.AddAffiantPolicies(p => p
            .AddStandingOrder<Beta1ShapeOrder>()
            .SetRiskScoreCalculator<AlwaysLowCalculator>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var policy = Assert.Single(scope.ServiceProvider.GetServices<IApprovalPolicy>());

        Assert.Equal(ReviewRequirement.StandingOrder, (await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone))!.Requirement);
    }

    [Fact]
    public async Task A_beta1_shaped_order_with_no_ceiling_fires_without_any_calculator()
    {
        var services = HostServices();
        services.AddAffiantPolicies(p => p.AddStandingOrder<Beta1ShapeCeilinglessOrder>());

        using var scope = services.BuildServiceProvider().CreateScope();
        var policy = Assert.Single(scope.ServiceProvider.GetServices<IApprovalPolicy>());

        // No ceiling means the placeholder is never asked to score anything.
        Assert.Equal(ReviewRequirement.StandingOrder, (await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone))!.Requirement);
    }

    // ── The optional eager check ──────────────────────────────────────────────

    [Fact]
    public void ValidateStandingOrders_throws_for_a_misconfigured_order()
    {
        var services = HostServices();
        services.AddAffiantPolicies(p => p
            .AddStandingOrder<ByTheBookOrder>()
            .AddStandingOrder<Beta1ShapeOrder>()
            .AddDefaultReviewerConfirmation());

        var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => AffiantPolicies.ValidateStandingOrders(sp));

        Assert.Contains(nameof(Beta1ShapeOrder), ex.Message);
        Assert.Contains("SetRiskScoreCalculator<T>()", ex.Message);
    }

    [Fact]
    public void ValidateStandingOrders_passes_for_a_well_configured_set()
    {
        var services = HostServices();
        services.AddSingleton(new ThresholdConfig());
        services.AddAffiantPolicies(p => p
            .SetRiskScoreCalculator<AlwaysLowCalculator>()
            .AddStandingOrder<ByTheBookOrder>()
            .AddStandingOrder<ConfigDrivenOrder>()
            .AddStandingOrder<Beta1ShapeOrder>()
            .AddDefaultReviewerConfirmation());

        var sp = services.BuildServiceProvider();

        AffiantPolicies.ValidateStandingOrders(sp);
    }

    [Fact]
    public void ValidateStandingOrders_passes_when_no_order_declares_a_ceiling()
    {
        var services = HostServices();
        services.AddAffiantPolicies(p => p
            .AddStandingOrder<ByTheBookOrder>()
            .AddDefaultReviewerConfirmation());

        var sp = services.BuildServiceProvider();

        AffiantPolicies.ValidateStandingOrders(sp);
    }

    [Fact]
    public void ValidateStandingOrders_approves_nothing()
    {
        var services = HostServices();
        services.AddAffiantPolicies(p => p
            .SetRiskScoreCalculator<CountingCalculator>()
            .AddStandingOrder<ConfigDrivenOrder>());
        services.AddSingleton(new ThresholdConfig());

        var sp = services.BuildServiceProvider();

        AffiantPolicies.ValidateStandingOrders(sp);

        // No Affidavit was evaluated, so no score was ever computed.
        Assert.Equal(0, CountingCalculator.Calls);
    }

    [Fact]
    public void ValidateStandingOrders_rejects_a_null_provider()
        => Assert.Throws<ArgumentNullException>(() => AffiantPolicies.ValidateStandingOrders(null!));

    // ── The by-the-book order, end to end through DI ──────────────────────────

    [Fact]
    public async Task A_by_the_book_order_still_fires_with_no_calculator_registered()
    {
        var services = HostServices();
        services.AddAffiantPolicies(p => p
            .AddStandingOrder<ByTheBookOrder>()
            .AddDefaultReviewerConfirmation());

        using var scope = services.BuildServiceProvider().CreateScope();
        var policies = scope.ServiceProvider.GetServices<IApprovalPolicy>().ToList();

        Assert.Equal(ReviewRequirement.StandingOrder, (await policies[0].EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone))!.Requirement);
    }

    [Fact]
    public async Task A_directly_constructed_order_still_scores_against_its_ceiling()
    {
        var policy = new ConfigDrivenOrder(
            new ThresholdConfig { Ceiling = (int)RiskLevel.Medium },
            new FixedScoreCalculator((int)RiskLevel.Medium));

        Assert.Equal(ReviewRequirement.StandingOrder, (await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone))!.Requirement);
    }

    // ── The placeholder scorer's own lifetime ──────────────────────────────────

    /// <summary>
    /// The placeholder <c>MissingRiskScoreCalculator</c> must itself be a Singleton. It carries no
    /// state and every call throws, so there is nothing scope-shaped about it — but if it were
    /// Scoped, a Standing Order the host legitimately registers Singleton (because it holds no
    /// per-request state of its own) and whose constructor still requires
    /// <see cref="RiskScoreCalculatorBase"/> — the beta.1 shape — would become captive to a
    /// shorter-lived dependency purely by resolving against the placeholder. <c>ValidateScopes</c>
    /// + <c>ValidateOnBuild</c> is exactly the setting combination (mirroring ASP.NET Core's
    /// Development host) that turns a captive dependency into a build-time failure, so building
    /// under it here is the proof: this order declares no <c>RiskThreshold</c> at all, so nothing
    /// about its own behaviour depends on the calculator's lifetime — only the container's
    /// captive-dependency check does.
    /// </summary>
    [Fact]
    public async Task Singleton_standing_order_needing_the_placeholder_builds_and_evaluates_under_ValidateOnBuild()
    {
        var services = HostServices();
        services.AddAffiantPolicies(p => p
            .AddStandingOrder<Beta1ShapeCeilinglessOrder>(ServiceLifetime.Singleton));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        var policy = Assert.Single(provider.GetServices<IApprovalPolicy>());

        Assert.Equal(ReviewRequirement.StandingOrder, (await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone))!.Requirement);
    }
}
