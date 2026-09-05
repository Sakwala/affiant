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

    /// <summary>Declares a ceiling and never matches — proves the check runs before the match.</summary>
    private sealed class NeverMatchingCeilingOrder : StandingOrderBase
    {
        public bool MatcherWasCalled { get; private set; }

        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
        {
            MatcherWasCalled = true;
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Threshold comes from a field this class's own constructor body assigns, so it is still
    /// null while <c>StandingOrderBase</c>'s constructor runs.
    /// </summary>
    private sealed class LateThresholdOrder : StandingOrderBase
    {
        private readonly int? _ceiling;

        public LateThresholdOrder() : base(null)
        {
            _ceiling = (int)RiskLevel.Low;
        }

        protected override int? RiskThreshold => _ceiling;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Affidavit EmptyAffidavit() => new(
        OperationType: "Test",
        EntityType: "TestEntity",
        EntityId: null,
        Fields: [new AffidavitField("field", "value", null,
            ProvenanceChain.From(ProvenanceTag.FromTool("fixture")))],
        AggregateConfidence: 0.9f,
        PopulatedConfidence: 0.9f,
        EmptyFieldCount: 0,
        Warnings: [],
        RequiresConfirmation: false);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_returns_null_when_conditions_do_not_match()
    {
        var policy = new NeverMatchingOrder();

        var result = await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone);

        Assert.Null(result);
    }

    [Fact]
    public async Task By_the_book_StandingOrder_auto_approves_on_the_match_alone()
    {
        // Subclass overriding only MatchesAsync, no risk ceiling, no calculator anywhere.
        var policy = new AlwaysMatchingOrder();

        var result = await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone);

        Assert.Equal(ReviewRequirement.StandingOrder, result!.Requirement);
    }

    [Fact]
    public async Task Declaring_a_threshold_without_a_calculator_throws_on_first_evaluation()
    {
        // Construction succeeds — RiskThreshold is virtual and a derived class may not have
        // assigned the state it reads until its own constructor has run.
        var policy = new LowCeilingOrder();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone));

        Assert.Contains(nameof(LowCeilingOrder), ex.Message);
        Assert.Contains("SetRiskScoreCalculator<T>()", ex.Message);
    }

    [Fact]
    public async Task The_configuration_check_runs_before_the_conditions_are_tested()
    {
        // A misconfigured order must not be able to answer "no match" and slip past the check.
        var policy = new NeverMatchingCeilingOrder();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone));

        Assert.Contains("SetRiskScoreCalculator<T>()", ex.Message);
        Assert.False(policy.MatcherWasCalled);
    }

    [Fact]
    public async Task A_threshold_assigned_after_the_base_constructor_still_reaches_the_check()
    {
        // RiskThreshold reads a field the derived constructor body assigns, so it is null while
        // the base constructor runs and non-null by the time EvaluateAsync reads it.
        var policy = new LateThresholdOrder();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone));

        Assert.Contains(nameof(LateThresholdOrder), ex.Message);
        Assert.Contains("SetRiskScoreCalculator<T>()", ex.Message);
    }

    [Fact]
    public async Task EvaluateAsync_returns_StandingOrder_when_score_is_at_or_below_the_threshold()
    {
        var policy = new LowCeilingOrder(new FixedScoreCalculator((int)RiskLevel.Low));

        var result = await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone);

        Assert.Equal(ReviewRequirement.StandingOrder, result!.Requirement);
    }

    /// <summary>
    /// An order held back by its own ceiling <b>degrades</b> rather than returning null (GT-5). The
    /// order matched and had an opinion; returning null would let a later policy speak as though
    /// this one never fired, and would leave the record unable to tell a Standing Order that was
    /// held back from a policy that simply asked for confirmation.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_degrades_to_the_reviewer_when_risk_exceeds_threshold()
    {
        var policy = new LowCeilingOrder(new FixedScoreCalculator((int)RiskLevel.High));

        var result = await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone);

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, result!.Requirement);
        Assert.Equal(ReviewRequirement.StandingOrder, result.DegradedFrom);
        Assert.Equal(StandingOrderBlockedReasons.RiskAboveThreshold, result.BlockedReason);
    }

    [Fact]
    public async Task EvaluateAsync_auto_approves_high_risk_when_threshold_is_high()
    {
        var policy = new HighCeilingOrder(new FixedScoreCalculator((int)RiskLevel.High));

        var result = await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone);

        Assert.Equal(ReviewRequirement.StandingOrder, result!.Requirement);
    }

    [Fact]
    public async Task EvaluateAsync_propagates_cancellation_token_to_matcher()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var policy = new NeverMatchingOrder();

        // NeverMatchingOrder completes synchronously; even with a cancelled token it returns null.
        var result = await policy.EvaluateAsync(EmptyAffidavit(), TestIdentities.Anyone, cts.Token);

        Assert.Null(result);
    }
}
