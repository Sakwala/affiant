using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Affiant.Abstractions.Serialization;

/// <summary>
/// Writes every instant the framework puts on the wire in one spelling: UTC, milliseconds, a
/// trailing <c>Z</c> — <c>2026-09-04T09:12:00.000Z</c>.
///
/// <para>
/// <b>Why one spelling.</b> The canonical form (SR-1) is a byte sequence two independent
/// implementations must produce identically, and an instant is part of it: a provenance tag carries
/// when it was minted, and a reviewer-act binding carries when the decision happened. .NET's
/// round-trip default writes <c>2026-09-04T09:12:00+00:00</c> and JavaScript's
/// <c>Date.toISOString()</c> writes <c>2026-09-04T09:12:00.000Z</c>. Both name the same instant and
/// both parse everywhere; they are different <i>bytes</i>, so they are different hashes, so a
/// framework that let either through could not agree with itself about what was sworn to. This
/// converter picks the JavaScript spelling because the schemas' own examples and the protocol's
/// canonical vectors are written in it.
/// </para>
///
/// <para>
/// <b>What it costs.</b> Sub-millisecond precision does not survive: a
/// <see cref="DateTimeOffset"/> carrying ticks below a millisecond is written truncated, exactly as
/// <c>toISOString()</c> truncates. Nothing in the protocol is specified to finer than a
/// millisecond, and a canonical form cannot carry a precision one of its implementations has no way
/// to express.
/// </para>
///
/// <para>
/// Reading is deliberately permissive — any RFC 3339 form round-trips in, including the
/// <c>+00:00</c> offset earlier releases wrote — so a record written by an older build still loads.
/// A host-supplied instant carrying a non-UTC offset is converted to UTC on write; the instant is
/// preserved, the offset is not, because an offset is a rendering choice and the record swears to
/// a point in time.
/// </para>
/// </summary>
public sealed class IsoInstantJsonConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>The one format string: UTC, milliseconds, a literal <c>Z</c>.</summary>
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Render <paramref name="value"/> the way every Affiant envelope spells an instant.</summary>
    public static string ToWire(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"An instant is an RFC 3339 date-time string; found {reader.TokenType}.");

        var text = reader.GetString();
        return DateTimeOffset.Parse(
            text!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(ToWire(value));
    }
}
