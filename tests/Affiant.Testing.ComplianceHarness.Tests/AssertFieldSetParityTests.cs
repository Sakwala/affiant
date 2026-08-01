namespace Affiant.Testing.ComplianceHarness.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Xunit;

/// <summary>
/// Tests for the opt-in <see cref="ComplianceHarness.AssertFieldSetParity"/> (P7, area-1 wave).
/// Does not run inside <see cref="ComplianceHarness.Verify"/> — exercised directly here.
/// </summary>
public class AssertFieldSetParityTests
{
    private sealed class MixedStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "WorkOrder";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Title", "string", "Title"),
            new("Priority", "string", "Priority"),
            new("TailNumber", "string", "Tail number mentioned in conversation", Projected: false),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    // --- Positive: every Projected card field is consumed → passes ---

    [Fact]
    public void AllCardFieldsConsumed_Passes()
    {
        var result = ComplianceHarness.AssertFieldSetParity(new MixedStrategy(), ["Title", "Priority"]);

        Assert.True(result.Passed);
        Assert.Empty(result.Errors);
    }

    // --- Negative: a card field the write path never reads fails with a precise message ---

    [Fact]
    public void UnconsumedCardField_FailsWithPreciseMessage()
    {
        var result = ComplianceHarness.AssertFieldSetParity(new MixedStrategy(), ["Title"]); // "Priority" unconsumed

        Assert.False(result.Passed);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Priority", error.FieldName);
        Assert.Contains("Priority", error.Reason);
        Assert.Contains("Projected=false", error.Reason);
    }

    // --- Extraction-exempt: Projected=false fields are never flagged even when unconsumed ---

    [Fact]
    public void ExtractionField_ExemptFromParityCheck_EvenWhenUnconsumed()
    {
        var result = ComplianceHarness.AssertFieldSetParity(new MixedStrategy(), ["Title", "Priority"]);

        Assert.True(result.Passed);
        Assert.DoesNotContain(result.Errors, e => e.FieldName == "TailNumber");
        Assert.DoesNotContain(result.Warnings, w => w.FieldName == "TailNumber");
    }

    // --- Warn-level: a consumed name the strategy never declares is informational only ---

    [Fact]
    public void ConsumedButUndeclaredField_ProducesWarning_DoesNotFailPassed()
    {
        var result = ComplianceHarness.AssertFieldSetParity(new MixedStrategy(), ["Title", "Priority", "Mystery"]);

        Assert.True(result.Passed);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Mystery", warning.FieldName);
    }

    [Fact]
    public void NullStrategy_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ComplianceHarness.AssertFieldSetParity(null!, []));
    }

    [Fact]
    public void NullConsumedFieldNames_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ComplianceHarness.AssertFieldSetParity(new MixedStrategy(), null!));
    }
}
