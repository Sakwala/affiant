namespace Affiant.Policies.Tests.Services;

using Affiant.Abstractions.Models;
using Affiant.Policies.Services;
using Xunit;

public class RiskScoreCalculatorBaseTests
{
    /// <summary>
    /// The shape a host writes: the framework supplies no formula, so the subclass is where
    /// scoring lives. This one is the "value over fifty is high" rule a host might author.
    /// </summary>
    private sealed class ValueMagnitudeCalculator : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
        {
            var valueField = affidavit.Fields.FirstOrDefault(f => f.Name == "Value");

            var score = valueField?.Value switch
            {
                decimal d when d > 50m => (int)RiskLevel.High,
                decimal => (int)RiskLevel.Medium,
                null => (int)RiskLevel.Low,
                _ => (int)RiskLevel.Medium
            };

            return Task.FromResult(score);
        }
    }

    private readonly RiskScoreCalculatorBase _calculator = new ValueMagnitudeCalculator();

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
    [InlineData(5.0, 2)]
    [InlineData(50.0, 2)]
    [InlineData(51.0, 3)]
    [InlineData(100.0, 3)]
    public async Task Host_subclass_supplies_the_score(decimal value, int expectedScore)
    {
        var affidavit = MakeAffidavit(ValueField(value));

        var score = await _calculator.ComputeAsync(affidavit);

        Assert.Equal(expectedScore, score);
    }

    [Fact]
    public async Task Host_subclass_can_return_the_lowest_band()
    {
        var affidavit = MakeAffidavit();

        var score = await _calculator.ComputeAsync(affidavit);

        Assert.Equal((int)RiskLevel.Low, score);
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
    public async Task ComputeAsync_accepts_a_cancellation_token()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var affidavit = MakeAffidavit(ValueField(100m));
        var score = await _calculator.ComputeAsync(affidavit, cts.Token);

        Assert.Equal((int)RiskLevel.High, score);
    }
}
