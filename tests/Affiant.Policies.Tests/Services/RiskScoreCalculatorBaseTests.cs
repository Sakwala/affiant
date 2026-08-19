namespace Affiant.Policies.Tests.Services;

using Affiant.Abstractions.Models;
using Affiant.Policies.Services;
using Xunit;

public class RiskScoreCalculatorBaseTests
{
    private readonly RiskScoreCalculatorBase _calculator = new DefaultRiskScoreCalculator();

    private static Affidavit MakeAffidavit(params AffidavitField[] fields) => new(
        OperationType: "Test",
        EntityType: "TestEntity",
        EntityId: null,
        Fields: fields,
        AggregateConfidence: 1.0f,
        Warnings: [],
        RequiresConfirmation: false);

    private static AffidavitField ValueField(decimal value) =>
        new("Value", value, null, ProvenanceChain.From(ProvenanceTag.Empty));

    [Theory]
    [InlineData(5.0, 2)]    // $5 → Medium
    [InlineData(25.0, 2)]   // $25 → Medium
    [InlineData(50.0, 2)]   // $50 boundary → Medium (not strictly greater than)
    [InlineData(51.0, 3)]   // $51 → High
    [InlineData(100.0, 3)]  // $100 → High
    public async Task ComputeAsync_scores_by_decimal_value_field(decimal value, int expectedScore)
    {
        var affidavit = MakeAffidavit(ValueField(value));

        var score = await _calculator.ComputeAsync(affidavit);

        Assert.Equal(expectedScore, score);
    }

    [Fact]
    public async Task ComputeAsync_returns_medium_when_no_value_field_present()
    {
        var affidavit = MakeAffidavit(new AffidavitField("Description", "no value field", null,
            ProvenanceChain.From(ProvenanceTag.Empty)));

        var score = await _calculator.ComputeAsync(affidavit);

        Assert.Equal((int)RiskLevel.Medium, score);
    }

    [Fact]
    public async Task ComputeAsync_returns_medium_for_empty_fields()
    {
        var affidavit = MakeAffidavit();

        var score = await _calculator.ComputeAsync(affidavit);

        Assert.Equal((int)RiskLevel.Medium, score);
    }

    [Theory]
    [InlineData(1, RiskLevel.Low)]
    [InlineData(2, RiskLevel.Medium)]
    [InlineData(3, RiskLevel.High)]
    [InlineData(4, RiskLevel.High)]  // Clamped above
    [InlineData(0, RiskLevel.Low)]   // Clamped below
    [InlineData(-5, RiskLevel.Low)]  // Clamped far below
    public void ClassifyScore_clamps_and_classifies(int score, RiskLevel expected)
    {
        var result = _calculator.ClassifyScore(score);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ComputeAsync_respects_cancellation_token()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Default implementation does not await anything cancelable, but the signature
        // must accept the token without throwing for basic cancellation.
        var affidavit = MakeAffidavit();
        var score = await _calculator.ComputeAsync(affidavit, cts.Token);

        Assert.Equal((int)RiskLevel.Medium, score);
    }
}
