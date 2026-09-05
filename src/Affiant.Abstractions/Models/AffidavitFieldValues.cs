namespace Affiant.Abstractions.Models;

using System.Text.Json;

/// <summary>
/// Turns the raw JSON a Docket row was stored as back into the CLR values the fields were filed
/// with, so a record that has been through a store scores, compares and reads exactly as it did
/// before it was written.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this closes.</b> An <see cref="AffidavitField.Value"/> is <c>object?</c>, so
/// serializing an Affidavit and reading it back yields a <see cref="JsonElement"/> in every field —
/// never the <c>decimal</c>, <c>string</c> or <c>bool</c> the projection put there. A host risk
/// scorer that pattern-matches on the value's type therefore sees an unrecognised type for every
/// field of every row that came from the store, and falls through to whatever its default branch
/// says. The same content then scores one way the first time it is filed and another way when it is
/// resubmitted — and resubmission is the one path that always reads the record back out of the
/// store. A policy whose verdict changes because of where the record was standing is not a policy.
/// </para>
/// <para>
/// <b>The declared kind decides, and the JSON decides the rest.</b> A field says what it is —
/// <see cref="AffidavitFieldKind.Number"/>, <see cref="AffidavitFieldKind.Date"/>,
/// <see cref="AffidavitFieldKind.Text"/>, <see cref="AffidavitFieldKind.Enum"/> — and that
/// declaration wins, because it is the same declaration the reviewer's card and the host's schema
/// were built from. Where the kind says nothing useful (a JSON number under a <c>text</c> field, a
/// boolean, an array, an object) the JSON's own shape decides: an integral number becomes
/// <c>long</c>, a fractional one <c>decimal</c> where it round-trips exactly and <c>double</c>
/// otherwise, a string stays a string, an array becomes <c>object?[]</c> and an object a
/// dictionary — recursively, so no <see cref="JsonElement"/> survives anywhere in the tree.
/// </para>
/// <para>
/// <b>It never invents.</b> A value that cannot be read as its declared kind — the string
/// <c>"tomorrow"</c> under a <c>date</c> field — is kept as the string it is rather than dropped or
/// coerced to a wrong instant: this is a rehydration, not a validation pass, and losing the filed
/// value would be worse than handing a scorer a string it can see is a string. Anything that is
/// already a CLR value (an Affidavit that never went near a store) is returned untouched.
/// </para>
/// </remarks>
public static class AffidavitFieldValues
{
    /// <summary>
    /// <paramref name="affidavit"/> with every field's value and previous value read back as CLR
    /// values. Returns the same instance when nothing needed converting, so a record that never
    /// went through a store costs one pass and no allocation.
    /// </summary>
    public static Affidavit Typed(Affidavit affidavit)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        AffidavitField[]? converted = null;
        for (var i = 0; i < affidavit.Fields.Length; i++)
        {
            var field = affidavit.Fields[i];
            var value = Typed(field.Value, field.Kind);
            var previous = Typed(field.PreviousValue, field.Kind);
            if (ReferenceEquals(value, field.Value) && ReferenceEquals(previous, field.PreviousValue))
                continue;

            converted ??= [.. affidavit.Fields];
            converted[i] = field with { Value = value, PreviousValue = previous };
        }

        return converted is null ? affidavit : affidavit with { Fields = converted };
    }

    /// <summary>
    /// <paramref name="amendments"/> with every value read back as a CLR value. A <c>null</c> under
    /// a key means "cleared" and stays <c>null</c>; an absent key is untouched by construction.
    /// </summary>
    /// <remarks>
    /// An amendment map that has been through a store carries the same
    /// <see cref="JsonElement"/> problem the fields do, and it reaches a scorer by a shorter route:
    /// a resubmission prefills these values into the new proposal's fields.
    /// </remarks>
    public static IReadOnlyDictionary<string, object?>? Typed(
        IReadOnlyDictionary<string, object?>? amendments)
    {
        if (amendments is null) return null;

        Dictionary<string, object?>? converted = null;
        foreach (var (name, value) in amendments)
        {
            var typed = Typed(value, kind: null);
            if (ReferenceEquals(typed, value)) continue;

            converted ??= new Dictionary<string, object?>(amendments, StringComparer.Ordinal);
            converted[name] = typed;
        }

        return converted ?? amendments;
    }

    /// <summary>
    /// One value, read back as a CLR value. Anything that is not a <see cref="JsonElement"/> is
    /// returned as it is.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <param name="kind">
    /// The field's declared <see cref="AffidavitFieldKind"/>, or <c>null</c> when there is no
    /// declaration to honour (an amendment map, which names fields but not kinds).
    /// </param>
    public static object? Typed(object? value, string? kind)
    {
        if (value is not JsonElement element) return value;
        return Read(element, kind);
    }

    private static object? Read(JsonElement element, string? kind) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => ReadNumber(element),
        JsonValueKind.String => ReadString(element, kind),
        JsonValueKind.Array => ReadArray(element),
        JsonValueKind.Object => ReadObject(element),
        _ => element.ToString(),
    };

    /// <summary>
    /// A JSON number as the narrowest CLR type that holds it exactly: <c>long</c> for an integer,
    /// <c>decimal</c> for a fractional value that round-trips, <c>double</c> otherwise.
    /// </summary>
    /// <remarks>
    /// <c>decimal</c> before <c>double</c> because the values a write proposal carries are money,
    /// quantities and rates, where a binary-floating-point representation of <c>0.1</c> is a defect
    /// a reviewer can see on the card.
    /// </remarks>
    private static object ReadNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var integral)) return integral;
        if (element.TryGetDecimal(out var exact)) return exact;
        return element.GetDouble();
    }

    /// <summary>
    /// A JSON string, read as the kind the field declared. A <c>date</c> field whose stored text is
    /// not an instant keeps its text: a rehydration never discards what was filed.
    /// </summary>
    private static object? ReadString(JsonElement element, string? kind)
    {
        var text = element.GetString();
        if (text is null) return null;

        return kind switch
        {
            AffidavitFieldKind.Date when DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var instant) => instant,
            AffidavitFieldKind.Number when decimal.TryParse(
                text,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number) => number,
            _ => text,
        };
    }

    private static object?[] ReadArray(JsonElement element)
    {
        var items = new object?[element.GetArrayLength()];
        var i = 0;
        foreach (var item in element.EnumerateArray())
            items[i++] = Read(item, kind: null);
        return items;
    }

    private static Dictionary<string, object?> ReadObject(JsonElement element)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            map[property.Name] = Read(property.Value, kind: null);
        return map;
    }
}
