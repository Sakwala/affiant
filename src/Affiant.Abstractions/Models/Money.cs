using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Affiant.Abstractions.Models;

/// <summary>
/// A monetary value as it appears on the wire: a decimal <b>string</b> and an ISO 4217 alphabetic
/// currency code.
///
/// <para>
/// <b>SR-2</b> — <i>a monetary field value is <c>{ amount: "&lt;decimal string&gt;", currency:
/// "&lt;ISO 4217&gt;" }</c>; never a binary float, and a JSON number where money is expected is a
/// type error.</i>
/// </para>
///
/// <para>
/// The reason is not fussiness about types. An Affidavit is a record a person swears to and an
/// auditor reads back years later; no binary float represents <c>0.10</c>, so a card that showed
/// "£4,000.10" and a store that holds <c>4000.099999999999</c> disagree about what was approved,
/// and nothing in the record says which one the reviewer saw. A decimal string is the value the
/// reviewer read, byte for byte, and it survives every JSON parser in every language unchanged —
/// including amounts no <see cref="decimal"/> can hold.
/// </para>
///
/// <para>
/// This is a <b>wire</b> rule. A host stores what it likes — integer minor units, a database
/// <c>decimal</c>, its own money type — and converts at the edge; the store persists the wire value
/// without reinterpreting it. That is also why <see cref="Amount"/> is not parsed into a number
/// here: parsing normalises, and <c>"10.00"</c> becoming <c>"10"</c> would change the canonical
/// bytes (SR-1) of a value nobody amended.
/// </para>
///
/// <para>
/// <b>No currency list is embedded.</b> ISO 4217 changes — currencies are added, redenominated and
/// withdrawn — and a table frozen into a serialization type would be wrong within a year and
/// unfixable without a release. What is checked here is the <i>shape</i>: three uppercase ASCII
/// letters. Membership in the standard's current list is the host's check.
/// </para>
///
/// <para>
/// An <see cref="AffidavitField.Value"/> or <see cref="AffidavitField.PreviousValue"/> holding one
/// of these serializes as the two strings, and a canonical form (SR-1) writes it the same way.
/// </para>
/// </summary>
/// <param name="Amount">
/// The amount as a decimal string: an optional <c>-</c>, an integer part with no leading zeros, and
/// an optional fractional part. No exponent, no thousands separators, no leading <c>+</c>, no
/// currency symbol. Examples: <c>"0"</c>, <c>"10.00"</c>, <c>"-1250"</c>,
/// <c>"123456789012345678901234567890.99"</c>.
/// </param>
/// <param name="Currency">
/// The ISO 4217 alphabetic code: three uppercase ASCII letters. Never case-folded on the wire
/// (SR-3).
/// </param>
[JsonConverter(typeof(MoneyJsonConverter))]
public sealed partial record Money(string Amount, string Currency)
{
    /// <summary>
    /// The shape <see cref="Amount"/> must match.
    ///
    /// Read it left to right: an optional minus; then either a bare <c>0</c> or a digit sequence
    /// that does not start with <c>0</c>; then, optionally, a decimal point and at least one digit.
    /// That excludes exactly the forms that lose or hide information — <c>1e3</c> (an exponent a
    /// reader has to evaluate), <c>1,000</c> (a separator that means a decimal point in half the
    /// world), <c>+10</c> (a sign JSON numbers do not carry), <c>010</c> (an integer part whose
    /// leading zero could be a truncation), <c>10.</c> and <c>.5</c> (a point with nothing on one
    /// side of it).
    ///
    /// Not anchored to a scale: SR-2's minor-unit clause is the host's to declare, and
    /// <see cref="ScaleFits"/> is how it declares one.
    /// </summary>
    public static Regex AmountPattern { get; } = AmountRegex();

    /// <summary>
    /// The shape <see cref="Currency"/> must match: the ISO 4217 alphabetic-code shape, three
    /// uppercase ASCII letters. Membership in the standard's current list is the host's check — see
    /// the type's own remarks.
    /// </summary>
    public static Regex CurrencyPattern { get; } = CurrencyRegex();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$")]
    private static partial Regex AmountRegex();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyRegex();

    /// <summary>
    /// The amount, validated on construction so an invalid <see cref="Money"/> cannot exist.
    /// </summary>
    /// <exception cref="ArgumentException">The amount is not a decimal string SR-2 admits.</exception>
    public string Amount { get; init; } = ValidAmount(Amount, nameof(Amount));

    /// <summary>
    /// The currency code, validated on construction so an invalid <see cref="Money"/> cannot exist.
    /// </summary>
    /// <exception cref="ArgumentException">The code is not three uppercase ASCII letters.</exception>
    public string Currency { get; init; } = ValidCurrency(Currency, nameof(Currency));

    /// <summary>
    /// Whether <paramref name="value"/> is a <see cref="Money"/>, or a JSON object carrying a valid
    /// <c>amount</c> and <c>currency</c> pair.
    ///
    /// A predicate, not a refusal — <see cref="Parse"/> is the refusing form and carries the
    /// diagnosis. Use this one where a value may legitimately not be money, such as when walking an
    /// <see cref="Affidavit"/>'s fields, whose values are <see cref="object"/>.
    /// </summary>
    public static bool IsMoney(object? value) => TryParse(value, out _);

    /// <summary>
    /// Read <paramref name="value"/> as money, or refuse it naming SR-2 and saying which part is
    /// wrong.
    ///
    /// The message names the rule because the caller who hits this is usually a host author who
    /// reached for the obvious thing — a <see cref="decimal"/> or a <see cref="double"/> — and the
    /// useful reply is not "invalid money" but "here is the rule, and here is why a float is not
    /// allowed to represent a price".
    /// </summary>
    /// <param name="value">The value that should be money: a <see cref="Money"/>, or a
    /// <see cref="System.Text.Json.Nodes.JsonObject"/> / <see cref="JsonElement"/> carrying the two
    /// properties.</param>
    /// <param name="where">What the value is, for the message: a field name, a JSON pointer.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not money.</exception>
    public static Money Parse(object? value, string where = "value")
    {
        if (TryParse(value, out var money))
            return money;

        throw new ArgumentException(Diagnosis(value, where), nameof(value));
    }

    /// <summary>
    /// Read <paramref name="value"/> as money without throwing.
    /// </summary>
    /// <returns><c>true</c> when <paramref name="value"/> is money; <c>false</c> otherwise.</returns>
    public static bool TryParse(object? value, out Money money)
    {
        money = null!;

        switch (value)
        {
            case Money already:
                money = already;
                return true;

            case System.Text.Json.Nodes.JsonObject node:
                return TryFromStrings(
                    node["amount"]?.GetValueKind() == JsonValueKind.String ? node["amount"]!.GetValue<string>() : null,
                    node["currency"]?.GetValueKind() == JsonValueKind.String ? node["currency"]!.GetValue<string>() : null,
                    out money);

            case JsonElement { ValueKind: JsonValueKind.Object } element:
                return TryFromStrings(
                    element.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null,
                    element.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                    out money);

            default:
                return false;
        }
    }

    /// <summary>
    /// Whether this amount fits a scale the host declares, in minor units.
    ///
    /// SR-2 caps an amount at "the currency's minor-unit scale unless the host declares otherwise",
    /// and this package holds no currency table (see the type's remarks), so the scale is a number
    /// the caller passes: <c>2</c> for sterling and the euro, <c>0</c> for the yen, <c>3</c> for the
    /// dinar, and whatever a host declares for an internal unit that needs more.
    ///
    /// The check is on the digits <b>written</b>, not on the value: <c>"10.00"</c> has scale 2 and
    /// fails <c>ScaleFits(1)</c> even though the amount is representable in one decimal place. That
    /// is deliberate — the record is what was written.
    /// </summary>
    /// <param name="minorUnits">The number of fractional digits the host allows.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minorUnits"/> is negative.</exception>
    public bool ScaleFits(int minorUnits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minorUnits);

        var point = Amount.IndexOf('.', StringComparison.Ordinal);
        var scale = point < 0 ? 0 : Amount.Length - point - 1;
        return scale <= minorUnits;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount} {Currency}");

    private static bool TryFromStrings(string? amount, string? currency, out Money money)
    {
        money = null!;
        if (amount is null || currency is null) return false;
        if (!AmountPattern.IsMatch(amount) || !CurrencyPattern.IsMatch(currency)) return false;

        money = new Money(amount, currency);
        return true;
    }

    private static string ValidAmount(string amount, string parameter)
    {
        ArgumentNullException.ThrowIfNull(amount, parameter);
        if (AmountPattern.IsMatch(amount)) return amount;

        throw new ArgumentException(
            $"SR-2: \"{amount}\" is not a decimal string. Expected {AmountPattern} — an optional " +
            "\"-\", an integer part with no leading zeros, and an optional fractional part; no " +
            "exponent, no thousands separators, no leading \"+\".",
            parameter);
    }

    private static string ValidCurrency(string currency, string parameter)
    {
        ArgumentNullException.ThrowIfNull(currency, parameter);
        if (CurrencyPattern.IsMatch(currency)) return currency;

        throw new ArgumentException(
            $"SR-2: \"{currency}\" is not an ISO 4217 code. Expected three uppercase ASCII letters " +
            "(the code is never case-folded on the wire).",
            parameter);
    }

    /// <summary>The sentence that says what is wrong with a value that should have been money.</summary>
    internal static string Diagnosis(object? value, string where)
    {
        if (value is sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal)
        {
            return
                $"SR-2: {where} is a number ({Convert.ToString(value, CultureInfo.InvariantCulture)}) " +
                "where money was expected. Money on the wire is { amount: \"<decimal string>\", " +
                "currency: \"<ISO 4217>\" }, never a binary float: 0.1 has no exact double, so the " +
                "amount a reviewer approved and the amount a store holds would differ with nothing " +
                "on the record to say which was sworn to.";
        }

        return
            $"SR-2: {where} is not money. Expected an object with an \"amount\" (a decimal string " +
            $"matching {AmountPattern}) and a \"currency\" (three uppercase ASCII letters); received " +
            $"{Describe(value)}.";
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"the string \"{(text.Length <= 40 ? text : text[..37] + "...")}\"",
        bool b => b ? "true" : "false",
        _ => $"a {value.GetType().Name}",
    };
}

/// <summary>
/// Writes a <see cref="Money"/> as its two strings and refuses a JSON number where money was
/// expected (SR-2).
///
/// <para>
/// The refusal is the point. Without it, a host that wrote <c>4000.10</c> into a money-shaped field
/// would get a silently lossy record — the failure mode SR-2 exists to prevent — and the loss would
/// surface years later, in an audit, as a card and a store that disagree.
/// </para>
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    /// <inheritdoc />
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            throw new JsonException(
                "SR-2: a JSON number where money was expected. Money on the wire is " +
                "{ \"amount\": \"<decimal string>\", \"currency\": \"<ISO 4217>\" }, never a binary " +
                "float: 0.1 has no exact double, so the amount a reviewer approved and the amount a " +
                "store holds would differ with nothing on the record to say which was sworn to.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"SR-2: a {reader.TokenType} token where money was expected. Money on the wire is " +
                "an object with an \"amount\" (a decimal string) and a \"currency\" (an ISO 4217 code).");
        }

        string? amount = null;
        string? currency = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case "amount":
                    amount = ReadStringOrRefuse(ref reader, "amount");
                    break;
                case "currency":
                    currency = ReadStringOrRefuse(ref reader, "currency");
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (amount is null || currency is null)
        {
            throw new JsonException(
                "SR-2: money carries both an \"amount\" and a \"currency\"; " +
                $"{(amount is null ? "\"amount\"" : "\"currency\"")} was missing.");
        }

        try
        {
            return new Money(amount, currency);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("amount", value.Amount);
        writer.WriteString("currency", value.Currency);
        writer.WriteEndObject();
    }

    private static string ReadStringOrRefuse(ref Utf8JsonReader reader, string property)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            throw new JsonException(
                $"SR-2: money's \"{property}\" is a JSON number. It is a string — a binary float " +
                "cannot hold the value a reviewer read.");
        }

        return reader.TokenType == JsonTokenType.String
            ? reader.GetString()!
            : throw new JsonException($"SR-2: money's \"{property}\" is a string; found {reader.TokenType}.");
    }
}
