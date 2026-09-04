namespace Affiant.Core.Tests.Serialization;

using System.Text.Json.Nodes;
using Affiant.Abstractions.Models;
using Affiant.Core.Serialization;
using Xunit;

/// <summary>
/// SR-1's clauses, one test per sentence — the cases no JSON fixture file can express, and the ones
/// the seven byte vectors pin only indirectly.
/// </summary>
public class CanonicalSerializerTests
{
    private static readonly Guid EntryId = Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001");
    private static readonly DateTimeOffset DecisionAt = new(2026, 9, 4, 9, 12, 0, TimeSpan.Zero);

    // ── Numbers ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1d, "1")]                                    // 1.0 is the number 1: one spelling per value.
    [InlineData(0.5d, "0.5")]
    [InlineData(-17d, "-17")]
    [InlineData(0d, "0")]
    [InlineData(1e21d, "1000000000000000000000")]            // positional, never "1e+21".
    [InlineData(1e-7d, "0.0000001")]
    [InlineData(-1.2345e-8d, "-0.000000012345")]
    [InlineData(0.30000000000000004d, "0.30000000000000004")] // every digit: rounding would decide what was sworn to.
    public void ANumberIsTheShortestRoundTripDecimalWrittenPositionally(double value, string expected) =>
        Assert.Equal(expected, CanonicalSerializer.Number(value));

    /// <summary>
    /// Negative zero is written <c>0</c>: JSON has no negative zero, and a reader cannot see the
    /// sign. Its own test rather than an <c>InlineData</c> row, because the compiler folds the
    /// literal <c>-0d</c> in an attribute to <c>0d</c> and the case would never run.
    /// </summary>
    [Fact]
    public void NegativeZeroIsWrittenAsZero()
    {
        Assert.True(double.IsNegative(double.NegativeZero));
        Assert.Equal("0", CanonicalSerializer.Number(double.NegativeZero));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonFiniteNumberIsRefused(double value)
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => CanonicalSerializer.Number(value));
        Assert.Contains("SR-1", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonFiniteNumberInsideADocumentIsRefused()
    {
        var document = new JsonObject { ["weight"] = JsonValue.Create(double.NaN) };
        Assert.Throws<InvalidOperationException>(() => CanonicalSerializer.CanonicalString(document));
    }

    // ── Keys, whitespace, and what is written ────────────────────────────────

    [Fact]
    public void KeysSortByCodePointAndNotByUtf16CodeUnit()
    {
        // U+E000 (private use) must precede U+1F600 (an emoji), because 0xE000 < 0x1F600. A
        // comparator over UTF-16 code units sees the emoji's leading surrogate 0xD83D and puts the
        // emoji first, which is the wrong way round.
        var document = new JsonObject { ["\U0001F600"] = 1, [""] = 2 };

        Assert.Equal("{\"\":2,\"\U0001F600\":1}", CanonicalSerializer.CanonicalString(document));
    }

    [Fact]
    public void ArrayOrderIsDataAndIsNeverSorted() =>
        Assert.Equal("[3,2,1]", CanonicalSerializer.CanonicalString(new JsonArray(3, 2, 1)));

    [Fact]
    public void ThereIsNoInsignificantWhitespace() =>
        Assert.Equal(
            "{\"a\":[1,2],\"b\":{\"c\":null}}",
            CanonicalSerializer.CanonicalString(
                JsonNode.Parse("{\n  \"b\" : { \"c\" : null },\n  \"a\" : [ 1, 2 ]\n}")));

    [Fact]
    public void NullIsWrittenAndAnAbsentPropertyIsOmitted()
    {
        // The distinction no JSON fixture file can express, because a file cannot hold "absent".
        // A field the record does not carry is not written; a field it carries holding null is.
        var present = new JsonObject { ["binding"] = null };
        Assert.Equal("{\"binding\":null}", CanonicalSerializer.CanonicalString(present));
        Assert.Equal("{}", CanonicalSerializer.CanonicalString(new JsonObject()));
    }

    [Fact]
    public void OnlyWhatJsonRequiresIsEscaped()
    {
        var document = new JsonObject
        {
            ["text"] = "quote \" backslash \\ tab \t unit  solidus a/b accented é emoji \U0001F600",
        };

        Assert.Equal(
            "{\"text\":\"quote \\\" backslash \\\\ tab \\t unit \\u001f solidus a/b accented é emoji \U0001F600\"}",
            CanonicalSerializer.CanonicalString(document));
    }

    // ── The accepted state ───────────────────────────────────────────────────

    [Fact]
    public void MoneyIsCanonicalizedAsItsTwoStringsAndNeverAsANumber()
    {
        var affidavit = Affidavit.Create(
            "WriteUpdate",
            "Invoice",
            "INV-1",
            [Field("Total", new Money("4000.10", "GBP"), mandatory: true)],
            warnings: []);

        Assert.Contains(
            "\"value\":{\"amount\":\"4000.10\",\"currency\":\"GBP\"}",
            CanonicalSerializer.CanonicalString(affidavit),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheFormIsTakenOverTheAcceptedStateAndNotOverTheProposal()
    {
        var proposal = Affidavit.Create(
            "WriteCreate", "Widget", null, [Field("Colour", "read", mandatory: true)], warnings: []);

        var amendments = new Dictionary<string, object?> { ["Colour"] = "red" };

        var asProposed = CanonicalSerializer.CanonicalHash(proposal);
        var asAccepted = CanonicalSerializer.CanonicalHash(proposal, amendments, EntryId, DecisionAt, "ana");

        Assert.NotEqual(asProposed, asAccepted);

        // ...and the accepted state is exactly what the amendment path produces, so a Docket row's
        // own amended record and this hash cannot disagree about the same decision.
        Assert.Equal(
            CanonicalSerializer.CanonicalHash(
                AffidavitAmendments.Apply(proposal, amendments, EntryId, DecisionAt, "ana")),
            asAccepted);
    }

    [Fact]
    public void NoAmendmentsIsTheSameAsNone()
    {
        var proposal = Affidavit.Create(
            "WriteCreate", "Widget", null, [Field("Colour", "red", mandatory: true)], warnings: []);

        Assert.Equal(
            CanonicalSerializer.CanonicalHash(proposal),
            CanonicalSerializer.CanonicalHash(proposal, null, EntryId, DecisionAt, "ana"));
        Assert.Equal(
            CanonicalSerializer.CanonicalHash(proposal),
            CanonicalSerializer.CanonicalHash(
                proposal, new Dictionary<string, object?>(), EntryId, DecisionAt, "ana"));
    }

    [Fact]
    public void AnAmendedFieldsChainCarriesTheReviewerActTagOnTopOfWhatItSuperseded()
    {
        var proposal = Affidavit.Create(
            "WriteCreate", "Widget", null, [Field("Colour", "read", mandatory: true)], warnings: []);

        var canonical = CanonicalSerializer.CanonicalString(
            proposal,
            new Dictionary<string, object?> { ["Colour"] = "red" },
            EntryId,
            DecisionAt,
            "ana");

        // The act names the decision — its entry and its instant (PV-2) — and the tag says when it
        // was minted, which is what an auditor follows years later.
        Assert.Contains("\"kind\":\"reviewer-act\"", canonical, StringComparison.Ordinal);
        Assert.Contains($"\"entryId\":\"{EntryId}\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"decisionAt\":\"2026-09-04T09:12:00.000Z\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"at\":\"2026-09-04T09:12:00.000Z\"", canonical, StringComparison.Ordinal);

        // The machine's displaced tag is preserved beneath it, never replaced.
        Assert.Contains("\"prior\":[{", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void AmendingAFieldTheDocumentDoesNotCarryIsRefused()
    {
        var document = (JsonObject)CanonicalSerializer.ToDocument(
            Affidavit.Create("WriteCreate", "Widget", null, [Field("Colour", "red")], warnings: []));

        var refusal = Assert.Throws<ArgumentException>(() =>
            CanonicalSerializer.ApplyAmendmentsForCanonical(
                document,
                new Dictionary<string, object?> { ["Shape"] = "round" },
                EntryId,
                DecisionAt,
                "ana"));

        Assert.Contains("Shape", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AClearedOptionalFieldLeavesTheFormAndAClearedMandatoryFieldStaysEmpty()
    {
        var document = (JsonObject)CanonicalSerializer.ToDocument(
            Affidavit.Create(
                "WriteCreate",
                "Widget",
                null,
                [Field("Colour", "red", mandatory: true), Field("Weight", 12.5, mandatory: false)],
                warnings: []));

        var amended = CanonicalSerializer.ApplyAmendmentsForCanonical(
            document,
            new Dictionary<string, object?> { ["Colour"] = null, ["Weight"] = null },
            EntryId,
            DecisionAt,
            "ana");

        var fields = amended["fields"]!.AsArray();
        Assert.Single(fields);
        Assert.Equal("Colour", fields[0]!["name"]!.GetValue<string>());
        Assert.Equal("Empty", fields[0]!["provenance"]!["current"]!["source"]!.GetValue<string>());

        // Clearing can never raise a number: the aggregate is recomputed over what is left (AF-4).
        Assert.Equal(0d, amended["aggregateConfidence"]!.GetValue<double>());
    }

    private static AffidavitField Field(string name, object? value, bool mandatory = false) =>
        new(
            name,
            value,
            null,
            ProvenanceChain.From(new ProvenanceTag(ProvenanceSource.Inferred, 0.6f, "the model guessed", 3)),
            mandatory);
}
