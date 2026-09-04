namespace Affiant.Policies.Tests.StandingOrders;

using Affiant.Abstractions.Models;
using Affiant.Policies.Services;
using Affiant.Policies.StandingOrders;
using Xunit;

/// <summary>
/// A Standing Order runs its three checks in the order GT-5 fixes: the empty required field, then
/// PV-4's binding check, then the risk comparison. The order is not decoration — the first two are
/// pure reads of the Affidavit, and running them first means a proposal with a hole in it never
/// costs a host's risk scorer a call.
/// </summary>
public class StandingOrderCheckOrderTests
{
    [Fact]
    public async Task AByTheBookOrder_Fires_WithNoScorerAnywhere()
    {
        var verdict = await new ByTheBookOrder().EvaluateAsync(Substantive());

        Assert.Equal(ReviewRequirement.StandingOrder, verdict!.Requirement);
        Assert.Null(verdict.BlockedReason);
    }

    [Fact]
    public async Task AMandatoryFieldReadingEmpty_HoldsTheOrderBack_AndTheScorerIsNeverCalled()
    {
        var scorer = new CountingCalculator();
        var order = new ScoredOrder(scorer);

        var verdict = await order.EvaluateAsync(WithEmptyMandatoryField());

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, verdict!.Requirement);
        Assert.Equal(StandingOrderBlockedReasons.MandatoryFieldEmpty, verdict.BlockedReason);
        Assert.Equal(0, scorer.Calls);
    }

    [Fact]
    public async Task AnUnboundDeclaredInput_HoldsTheOrderBack_AndTheScorerIsNeverCalled()
    {
        var scorer = new CountingCalculator();
        var order = new ExternalPredicatingOrder(scorer);

        var verdict = await order.EvaluateAsync(WithUnboundExternal());

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, verdict!.Requirement);
        Assert.Equal(StandingOrderBlockedReasons.UnboundDeclaredInput, verdict.BlockedReason);
        Assert.Equal(0, scorer.Calls);
    }

    [Fact]
    public async Task AScoreAboveTheCeiling_HoldsTheOrderBack_AfterTheScorerHasSpoken()
    {
        var scorer = new CountingCalculator((int)RiskLevel.High);
        var order = new ScoredOrder(scorer);

        var verdict = await order.EvaluateAsync(Substantive());

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, verdict!.Requirement);
        Assert.Equal(StandingOrderBlockedReasons.RiskAboveThreshold, verdict.BlockedReason);
        Assert.Equal(1, scorer.Calls);
    }

    [Fact]
    public async Task AScoreAtOrUnderTheCeiling_Fires()
    {
        var verdict = await new ScoredOrder(new CountingCalculator((int)RiskLevel.Low))
            .EvaluateAsync(Substantive());

        Assert.Equal(ReviewRequirement.StandingOrder, verdict!.Requirement);
    }

    /// <summary>
    /// The order's own review window survives a degrade: the degrade changes who decides, not when
    /// the window closes (GT-5, PV-4 — both say so in the same words).
    /// </summary>
    [Fact]
    public async Task ADegradedOrder_KeepsItsOwnReviewWindow()
    {
        var verdict = await new WindowedOrder().EvaluateAsync(WithEmptyMandatoryField());

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, verdict!.Requirement);
        Assert.Equal(TimeSpan.FromMinutes(9), verdict.TimeToLive);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

    private static Affidavit Substantive() =>
        Affidavit.Create("CreateOrder", "Order", null,
            [Field("title", "Q3 invoice", ProvenanceTag.FromTool("fixture"))], []);

    private static Affidavit WithEmptyMandatoryField() =>
        Affidavit.Create("CreateOrder", "Order", null,
        [
            Field("title", "Q3 invoice", ProvenanceTag.FromTool("fixture")),
            Field("supplier", null, ProvenanceTag.Empty, isMandatory: true),
        ], []);

    private static Affidavit WithUnboundExternal() =>
        Affidavit.Create("CreateOrder", "Order", null,
            [Field("balance", 4200, new ProvenanceTag(ProvenanceSource.External, 0.9f, "asserted", null))], []);

    private static AffidavitField Field(
        string name, object? value, ProvenanceTag tag, bool isMandatory = false) =>
        new(name, value, null, ProvenanceChain.From(tag), IsMandatory: isMandatory);

    private sealed class CountingCalculator(int score = (int)RiskLevel.Low) : RiskScoreCalculatorBase
    {
        public int Calls { get; private set; }

        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(score);
        }
    }

    private sealed class ByTheBookOrder : StandingOrderBase
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class ScoredOrder(RiskScoreCalculatorBase scorer) : StandingOrderBase(scorer)
    {
        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class ExternalPredicatingOrder(RiskScoreCalculatorBase scorer) : StandingOrderBase(scorer)
    {
        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override IReadOnlyCollection<ProvenanceSource> DeclaredInputs => [ProvenanceSource.External];

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class WindowedOrder : StandingOrderBase
    {
        protected override TimeSpan? StandingOrderTimeToLive => TimeSpan.FromMinutes(9);

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }
}
