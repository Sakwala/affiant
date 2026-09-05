namespace Affiant.Abstractions.Tests.Models;

using System.Text.Json;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Serialization;
using Xunit;

/// <summary>
/// SR-2's accept/reject table: money on the wire is a decimal string plus an ISO 4217 code, and a
/// JSON number where money was expected is a type error rather than a silently lossy record.
/// </summary>
public class MoneyTests
{
    // ── The amount ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("10")]
    [InlineData("10.00")]                                   // trailing zeros are what the reviewer read.
    [InlineData("-1250")]
    [InlineData("-0.01")]
    [InlineData("4000.10")]                                 // the amount no double holds exactly.
    [InlineData("123456789012345678901234567890.99")]       // and the one no decimal holds either.
    public void AnAmountJsonCanCarryIsAccepted(string amount) =>
        Assert.Equal(amount, new Money(amount, "GBP").Amount);

    [Theory]
    [InlineData("1e3", "an exponent a reader has to evaluate")]
    [InlineData("1,000", "a separator that means a decimal point in half the world")]
    [InlineData("+10", "a sign JSON numbers do not carry")]
    [InlineData("010", "an integer part whose leading zero could be a truncation")]
    [InlineData("10.", "a point with nothing after it")]
    [InlineData(".5", "a point with nothing before it")]
    [InlineData("£10", "a currency symbol")]
    [InlineData("", "nothing at all")]
    [InlineData(" 10 ", "surrounding space")]
    public void AnAmountThatLosesOrHidesInformationIsRefused(string amount, string why)
    {
        var refusal = Assert.Throws<ArgumentException>(() => new Money(amount, "GBP"));
        Assert.Contains("SR-2", refusal.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    // ── The currency ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GBP")]
    [InlineData("JPY")]
    [InlineData("LKR")]
    [InlineData("XYZ")]  // shape only: membership in the current ISO list is the host's check.
    public void AnIso4217ShapedCodeIsAccepted(string currency) =>
        Assert.Equal(currency, new Money("1", currency).Currency);

    [Theory]
    [InlineData("gbp")]   // never case-folded on the wire (SR-3) — and never case-folded INTO shape.
    [InlineData("GB")]
    [InlineData("GBPX")]
    [InlineData("G8P")]
    [InlineData("")]
    public void ACodeThatIsNotThreeUppercaseAsciiLettersIsRefused(string currency) =>
        Assert.Throws<ArgumentException>(() => new Money("1", currency));

    // ── The refusal that matters ─────────────────────────────────────────────

    [Theory]
    [InlineData("4000.10")]
    [InlineData("{\"amount\":4000.10,\"currency\":\"GBP\"}")]
    public void AJsonNumberWhereMoneyWasExpectedIsRefusedAndTheMessageNamesTheRule(string json)
    {
        var payload = json.StartsWith('{') ? json : json;
        var refusal = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Money>(payload, AffiantJson.SerializerOptions));

        Assert.Contains("SR-2", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalExplainsWhyAFloatIsNotAllowedToRepresentAPrice()
    {
        var refusal = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Money>("4000.10", AffiantJson.SerializerOptions));

        // The caller who hits this is a host author who reached for the obvious thing, and the
        // useful reply is the reason rather than "invalid money".
        Assert.Contains("binary float", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsingANumberRefusesAndNamesTheValue()
    {
        var refusal = Assert.Throws<ArgumentException>(() => Money.Parse(4000.10m, "Invoice.Total"));

        Assert.Contains("SR-2", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Invoice.Total", refusal.Message, StringComparison.Ordinal);
    }

    // ── The wire form ────────────────────────────────────────────────────────

    [Fact]
    public void MoneyIsWrittenAsItsTwoStrings() =>
        Assert.Equal(
            "{\"amount\":\"4000.10\",\"currency\":\"GBP\"}",
            JsonSerializer.Serialize(new Money("4000.10", "GBP"), AffiantJson.SerializerOptions));

    [Fact]
    public void MoneyRoundTripsWithoutNormalising()
    {
        // "10.00" stays "10.00" and never becomes "10": the trailing zeros are what the reviewer
        // saw, and dropping them would change the canonical bytes (SR-1) of a value nobody amended.
        const string wire = "{\"amount\":\"10.00\",\"currency\":\"GBP\"}";
        var money = JsonSerializer.Deserialize<Money>(wire, AffiantJson.SerializerOptions)!;

        Assert.Equal("10.00", money.Amount);
        Assert.Equal(wire, JsonSerializer.Serialize(money, AffiantJson.SerializerOptions));
    }

    [Fact]
    public void AnAffidavitFieldValueThatIsMoneyIsWrittenAsMoney()
    {
        var field = new AffidavitField(
            "Total",
            new Money("4000.10", "GBP"),
            new Money("40.00", "GBP"),
            ProvenanceChain.From(ProvenanceTag.Empty),
            IsMandatory: true,
            Kind: AffidavitFieldKind.Number);

        var json = JsonSerializer.Serialize(field, AffiantJson.SerializerOptions);

        Assert.Contains("\"value\":{\"amount\":\"4000.10\",\"currency\":\"GBP\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"previousValue\":{\"amount\":\"40.00\",\"currency\":\"GBP\"}", json, StringComparison.Ordinal);
    }

    // ── The scale a host declares ────────────────────────────────────────────

    [Theory]
    [InlineData("10.00", 2, true)]
    [InlineData("10.00", 1, false)]  // the check is on the digits WRITTEN, not on the value.
    [InlineData("1250", 0, true)]
    [InlineData("1250.5", 0, false)]
    [InlineData("1.234", 3, true)]
    public void AScaleTheHostDeclaresIsCheckedAgainstTheDigitsWritten(string amount, int minorUnits, bool fits) =>
        Assert.Equal(fits, new Money(amount, "GBP").ScaleFits(minorUnits));

    [Fact]
    public void ANegativeScaleIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money("1", "GBP").ScaleFits(-1));

    // ── The predicate ────────────────────────────────────────────────────────

    [Fact]
    public void IsMoneyRecognisesMoneyAndNothingElse()
    {
        Assert.True(Money.IsMoney(new Money("1", "GBP")));
        Assert.False(Money.IsMoney(4000.10m));
        Assert.False(Money.IsMoney("4000.10"));
        Assert.False(Money.IsMoney(null));
    }
}
