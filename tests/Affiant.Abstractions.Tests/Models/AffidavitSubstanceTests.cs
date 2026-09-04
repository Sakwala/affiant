namespace Affiant.Abstractions.Tests.Models;

using Affiant.Abstractions.Models;
using Xunit;

/// <summary>
/// The substance predicate (GT-3), tested directly because it is the shared one: the projection
/// reports it as telemetry today and the gate refuses on it once the runtime refusal lands. Each of
/// the three failure signatures is covered here, including the hollow one — a value under Empty
/// provenance — which the built-in projection cannot itself produce (an Empty tag never carries a
/// value through it) but a host-supplied <c>IAffidavitProjection</c> or a relay-supplied capture can.
/// </summary>
public class AffidavitSubstanceTests
{
    [Fact]
    public void NoFieldsAtAll_SwearsToNothing()
    {
        Assert.Equal(
            "the Affidavit swears to no fields",
            AffidavitSubstance.DescribeFailure(Affidavit([])));
    }

    [Fact]
    public void EveryFieldEmpty_SwearsToNothing()
    {
        var affidavit = Affidavit([
            Field("title", value: null, ProvenanceTag.Empty),
            Field("amount", value: null, ProvenanceTag.Empty),
        ]);

        Assert.Equal(
            "no proposed field carries provenance other than Empty",
            AffidavitSubstance.DescribeFailure(affidavit));
    }

    [Fact]
    public void AValueUnderEmptyProvenance_IsTheHollowSignature_AndNamesTheField()
    {
        var affidavit = Affidavit([
            Field("title", "Q3 invoice", ProvenanceTag.FromInference(InferenceSource.Inferred, "title", 0.9f)),
            Field("amount", 4200, ProvenanceTag.Empty),
        ]);

        Assert.Equal(
            "field \"amount\" carries a value with Empty provenance",
            AffidavitSubstance.DescribeFailure(affidavit));
    }

    /// <summary>
    /// A field with no value and no provenance is honest — "I do not know this" — and only becomes
    /// a substance failure when every field says it. A blank string is the same thing written down.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyValueUnderEmptyProvenance_IsNotHollow(string? value)
    {
        var affidavit = Affidavit([
            Field("title", "Q3 invoice", ProvenanceTag.FromInference(InferenceSource.Inferred, "title", 0.9f)),
            Field("amount", value, ProvenanceTag.Empty),
        ]);

        Assert.Null(AffidavitSubstance.DescribeFailure(affidavit));
    }

    [Fact]
    public void OneSubstantiveField_IsEnough()
    {
        var affidavit = Affidavit([
            Field("title", "Q3 invoice", ProvenanceTag.FromInference(InferenceSource.Inferred, "title", 0.9f)),
            Field("amount", value: null, ProvenanceTag.Empty),
        ]);

        Assert.Null(AffidavitSubstance.DescribeFailure(affidavit));
        Assert.True(AffidavitSubstance.IsSubstantive(affidavit));
    }

    /// <summary>
    /// Zero, false and an empty collection are values a user genuinely stated. Treating them as
    /// "no value" is the classic falsy-check bug, and here it would silently refuse a real proposal.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(false)]
    public void FalsyButRealValues_AreValues(object value)
    {
        var affidavit = Affidavit([Field("amount", value, ProvenanceTag.Empty)]);

        Assert.Equal(
            "field \"amount\" carries a value with Empty provenance",
            AffidavitSubstance.DescribeFailure(affidavit));
    }

    private static AffidavitField Field(string name, object? value, ProvenanceTag tag) =>
        new(name, value, null, ProvenanceChain.From(tag));

    private static Affidavit Affidavit(AffidavitField[] fields) =>
        Abstractions.Models.Affidavit.Create(
            operationType: "CreateInvoice",
            entityType: "Invoice",
            entityId: null,
            fields: fields,
            warnings: []);
}
