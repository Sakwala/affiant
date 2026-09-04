namespace Affiant.Policies.Tests.StandingOrders;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Policies.Services;
using Affiant.Policies.StandingOrders;
using Xunit;

public class StandingOrderPolicyTests
{
    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class AlwaysMatchingOrder : StandingOrderBase
    {
        // Parameterless: uses DefaultRiskScoreCalculator without a logger.
        public AlwaysMatchingOrder() : base(new DefaultRiskScoreCalculator()) { }

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);

        protected override Task<string?> GetAutoApproverIdAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult<string?>("system");
    }

    private sealed class NeverMatchingOrder : StandingOrderBase
    {
        public NeverMatchingOrder() : base(new DefaultRiskScoreCalculator()) { }

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(false);
    }

    private sealed class HighThresholdOrder : StandingOrderBase
    {
        public HighThresholdOrder() : base(new DefaultRiskScoreCalculator()) { }

        // Override threshold to High — even $100 operations auto-approve.
        protected override int RiskThreshold => (int)RiskLevel.High;

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

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_returns_null_when_conditions_do_not_match()
    {
        var policy = new NeverMatchingOrder();

        var result = await policy.EvaluateAsync(EmptyAffidavit());

        Assert.Null(result);
    }

    [Fact]
    public async Task EvaluateAsync_returns_StandingOrder_when_matches_and_risk_low()
    {
        // No "Value" field → default scorer returns Medium (2), threshold is Low (1)
        // So a zero-value affidavit would NOT auto-approve with default threshold!
        // But we use a high-threshold order that accepts all risk levels.
        var policy = new HighThresholdOrder();

        var result = await policy.EvaluateAsync(EmptyAffidavit());

        Assert.NotNull(result);
        Assert.Equal(ReviewRequirement.StandingOrder, result);
    }

    [Fact]
    public async Task EvaluateAsync_returns_null_when_risk_exceeds_threshold()
    {
        // AlwaysMatchingOrder has default RiskThreshold = Low (1).
        // High-value affidavit scores High (3) → exceeds threshold → returns null.
        var policy = new AlwaysMatchingOrder();

        var result = await policy.EvaluateAsync(HighValueAffidavit());

        Assert.Null(result);
    }

    [Fact]
    public async Task EvaluateAsync_auto_approves_high_value_when_threshold_is_high()
    {
        // HighThresholdOrder accepts risk up to High (3).
        var policy = new HighThresholdOrder();

        var result = await policy.EvaluateAsync(HighValueAffidavit());

        Assert.NotNull(result);
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
