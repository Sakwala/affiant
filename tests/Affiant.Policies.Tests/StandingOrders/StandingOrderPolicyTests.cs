namespace Affiant.Policies.Tests.StandingOrders;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Policies.Services;
using Affiant.Policies.StandingOrders;
using Xunit;

public class StandingOrderPolicyTests
{
    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class FixedScoreCalculator(int score) : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
            => Task.FromResult(score);
    }

    /// <summary>
    /// A Standing Order written by the book: subclass, implement MatchesAsync, nothing else.
    /// No risk ceiling declared, so no calculator is needed.
    /// </summary>
    private sealed class AlwaysMatchingOrder : StandingOrderBase
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);

        protected override Task<string?> GetAutoApproverIdAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult<string?>("system");
    }

    private sealed class NeverMatchingOrder : StandingOrderBase
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(false);
    }

    private sealed class LowCeilingOrder(RiskScoreCalculatorBase? riskScorer = null)
        : StandingOrderBase(riskScorer)
    {
        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class HighCeilingOrder(RiskScoreCalculatorBase riskScorer)
        : StandingOrderBase(riskScorer)
    {
        protected override int? RiskThreshold => (int)RiskLevel.High;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Affidavit EmptyAffidavit() => new(
        OperationType: "Test",
        EntityType: "TestEntity",
        EntityId: null,
        Fields: [],
        AggregateConfidence: 1.0f,
        Warnings: [],
        RequiresConfirmation: false);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_returns_null_when_conditions_do_not_match()
    {
        var policy = new NeverMatchingOrder();

        var result = await policy.EvaluateAsync(EmptyAffidavit());

        Assert.Null(result);
    }

    [Fact]
    public async Task By_the_book_StandingOrder_auto_approves_on_the_match_alone()
    {
        // Subclass overriding only MatchesAsync, no risk ceiling, no calculator anywhere.
        var policy = new AlwaysMatchingOrder();

        var result = await policy.EvaluateAsync(EmptyAffidavit());

        Assert.Equal(ReviewRequirement.StandingOrder, result);
    }

    [Fact]
    public void Declaring_a_threshold_without_a_calculator_throws_on_construction()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new LowCeilingOrder());

        Assert.Contains(nameof(LowCeilingOrder), ex.Message);
        Assert.Contains("SetRiskScoreCalculator<T>()", ex.Message);
    }

    [Fact]
    public async Task EvaluateAsync_returns_StandingOrder_when_score_is_at_or_below_the_threshold()
    {
        var policy = new LowCeilingOrder(new FixedScoreCalculator((int)RiskLevel.Low));

        var result = await policy.EvaluateAsync(EmptyAffidavit());

        Assert.Equal(ReviewRequirement.StandingOrder, result);
    }

    [Fact]
    public async Task EvaluateAsync_returns_null_when_risk_exceeds_threshold()
    {
        var policy = new LowCeilingOrder(new FixedScoreCalculator((int)RiskLevel.High));

        var result = await policy.EvaluateAsync(EmptyAffidavit());

        Assert.Null(result);
    }

    [Fact]
    public async Task EvaluateAsync_auto_approves_high_risk_when_threshold_is_high()
    {
        var policy = new HighCeilingOrder(new FixedScoreCalculator((int)RiskLevel.High));

        var result = await policy.EvaluateAsync(EmptyAffidavit());

        Assert.Equal(ReviewRequirement.StandingOrder, result);
    }

    [Fact]
    public async Task EvaluateAsync_propagates_cancellation_token_to_matcher()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var policy = new NeverMatchingOrder();

        // NeverMatchingOrder completes synchronously; even with a cancelled token it returns null.
        var result = await policy.EvaluateAsync(EmptyAffidavit(), cts.Token);

        Assert.Null(result);
    }
}
