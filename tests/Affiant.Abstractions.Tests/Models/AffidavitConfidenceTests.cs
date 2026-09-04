namespace Affiant.Abstractions.Tests.Models;

using Affiant.Abstractions.Models;
using Xunit;

/// <summary>
/// The three confidence numbers, one test per checkable sentence of the rule they implement.
///
/// The rule: the aggregate is the <b>minimum</b> over every proposed field's current tag with an
/// <c>Empty</c> field counting as 0 (so it is 0 exactly when some proposed field has unknown
/// provenance); the populated confidence is the minimum over the non-<c>Empty</c> fields, or null
/// when none is populated; the empty-field count is how many proposed fields read <c>Empty</c>.
/// </summary>
public class AffidavitConfidenceTests
{
    private static AffidavitField Field(string name, ProvenanceTag tag, bool mandatory = false) =>
        new(name, "v", null, ProvenanceChain.From(tag), mandatory);

    private static ProvenanceTag Tag(ProvenanceSource source, float confidence) =>
        new(source, confidence, null, null);

    [Fact]
    public void Aggregate_IsTheMinimumOverEveryProposedField_NotTheMean()
    {
        var numbers = AffidavitConfidence.Compute(
        [
            Field("a", Tag(ProvenanceSource.Inferred, 0.9f)),
            Field("b", Tag(ProvenanceSource.Inferred, 0.5f)),
            Field("c", Tag(ProvenanceSource.UserStated, 1.0f)),
        ]);

        // The mean would be 0.8.
        Assert.Equal(0.5f, numbers.AggregateConfidence, 5);
    }

    [Fact]
    public void Aggregate_CountsAnEmptyFieldAsZero_WhateverItsTagSays()
    {
        // A tag that claims Empty at 0.9 is forced to 0 by the tag itself; the aggregate would
        // count it as 0 regardless.
        var numbers = AffidavitConfidence.Compute(
        [
            Field("a", Tag(ProvenanceSource.UserStated, 1.0f)),
            Field("b", Tag(ProvenanceSource.Empty, 0.9f)),
        ]);

        Assert.Equal(0f, numbers.AggregateConfidence, 5);
    }

    [Fact]
    public void Aggregate_IsZero_WhenAProposedFieldHasUnknownProvenance()
    {
        var numbers = AffidavitConfidence.Compute(
        [
            Field("a", Tag(ProvenanceSource.UserStated, 1.0f)),
            Field("b", ProvenanceTag.Empty),
        ]);

        Assert.Equal(0f, numbers.AggregateConfidence, 5);
    }

    [Fact]
    public void Aggregate_IsNonZero_WhenNoProposedFieldHasUnknownProvenance()
    {
        // The other direction of "0 if and only if": nothing else drives it to 0.
        var numbers = AffidavitConfidence.Compute(
        [
            Field("a", Tag(ProvenanceSource.Default, 0.3f)),
            Field("b", Tag(ProvenanceSource.Inferred, 0.6f)),
        ]);

        Assert.Equal(0.3f, numbers.AggregateConfidence, 5);
        Assert.Equal(0, numbers.EmptyFieldCount);
    }

    [Fact]
    public void Populated_IsTheMinimumOverTheNonEmptyFields()
    {
        var numbers = AffidavitConfidence.Compute(
        [
            Field("a", Tag(ProvenanceSource.UserStated, 1.0f)),
            Field("b", Tag(ProvenanceSource.Inferred, 0.4f)),
            Field("c", ProvenanceTag.Empty),
        ]);

        Assert.Equal(0.4f, numbers.PopulatedConfidence!.Value, 5);
    }

    [Fact]
    public void Populated_IsNull_WhenNoFieldIsPopulated()
    {
        var numbers = AffidavitConfidence.Compute(
        [
            Field("a", ProvenanceTag.Empty),
            Field("b", ProvenanceTag.Empty),
        ]);

        // Null rather than 0: "there is nothing populated to be confident about" is a different
        // statement from "the populated fields are worthless".
        Assert.Null(numbers.PopulatedConfidence);
    }

    [Fact]
    public void EmptyFieldCount_CountsTheEmptyProposedFields()
    {
        var numbers = AffidavitConfidence.Compute(
        [
            Field("a", ProvenanceTag.Empty),
            Field("b", Tag(ProvenanceSource.Inferred, 0.6f)),
            Field("c", ProvenanceTag.Empty),
        ]);

        Assert.Equal(2, numbers.EmptyFieldCount);
    }

    [Fact]
    public void NoFieldsAtAll_ReportsZero_Null_Zero()
    {
        var numbers = AffidavitConfidence.Compute([]);

        Assert.Equal(0f, numbers.AggregateConfidence);
        Assert.Null(numbers.PopulatedConfidence);
        Assert.Equal(0, numbers.EmptyFieldCount);
    }

    [Fact]
    public void AffidavitCreate_ComputesAllThreeFromTheFields()
    {
        var affidavit = Affidavit.Create(
            "WriteCreate",
            "Widget",
            entityId: null,
            [
                Field("a", Tag(ProvenanceSource.UserStated, 1.0f)),
                Field("b", ProvenanceTag.Empty),
            ],
            warnings: []);

        Assert.Equal(0f, affidavit.AggregateConfidence, 5);
        Assert.Equal(1.0f, affidavit.PopulatedConfidence!.Value, 5);
        Assert.Equal(1, affidavit.EmptyFieldCount);
    }

    [Fact]
    public void WithFields_RecomputesAllThree()
    {
        var affidavit = Affidavit.Create(
            "WriteCreate",
            "Widget",
            entityId: null,
            [Field("a", Tag(ProvenanceSource.UserStated, 1.0f))],
            warnings: []);

        var amended = affidavit.WithFields(
        [
            Field("a", Tag(ProvenanceSource.UserStated, 1.0f)),
            Field("b", ProvenanceTag.Empty),
        ]);

        Assert.Equal(1.0f, affidavit.AggregateConfidence, 5);
        Assert.Equal(0f, amended.AggregateConfidence, 5);
        Assert.Equal(1, amended.EmptyFieldCount);
    }
}
