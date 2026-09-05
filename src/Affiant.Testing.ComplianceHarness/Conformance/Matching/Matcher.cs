using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Affiant.Testing.ComplianceHarness.Conformance.Matching;

/// <summary>
/// The matcher of <c>RUNNER.md</c> §5: every matcher is partial. A fixture states the facts its
/// rule is about and says nothing about the rest, so an unrelated addition to a Docket row does
/// not break thirty documents.
/// </summary>
/// <remarks>
/// <para>
/// <b>null is a fact; absent is not.</b> <c>"amendedAffidavit": null</c> asserts that no amendment
/// has been accepted; omitting the key asserts nothing. A driver that treated a stated null as
/// "unset" would turn a fixture that pins a rule into a fixture that passes on anything — so the
/// expectation and the observation are both JSON here, and a stated null is compared like any
/// other value.
/// </para>
/// <para>
/// Four clauses are not plain partial matches and are given their own comparison: a telemetry
/// clause is a membership test, a card's <c>warningsContain</c> is a substring test, a lineage
/// link may carry the <c>"@some"</c> sentinel, and a stated <c>fields</c> list asserts the field
/// list exactly, in order.
/// </para>
/// </remarks>
internal static class Matcher
{
    /// <summary>The sentinel that asserts a lineage link is present and nothing more.</summary>
    public const string SomeSentinel = "@some";

    /// <summary>Compare one clause of an expectation against the observation, appending every failure found.</summary>
    public static void Match(string at, JsonNode? expected, JsonNode? actual, List<Mismatch> into)
    {
        switch (expected)
        {
            case JsonObject eo when actual is JsonObject ao:
                foreach (var (key, value) in eo)
                {
                    Match(Join(at, key), value, ao.TryGetPropertyValue(key, out var found) ? found : Absent, into);
                }

                return;

            case JsonObject when actual is null:
                into.Add(new Mismatch(at, expected.DeepClone(), null));
                return;

            case JsonObject:
                into.Add(new Mismatch(at, expected.DeepClone(), actual?.DeepClone()));
                return;

            case JsonArray ea when actual is JsonArray aa:
                // Arrays by length then element-wise. A stated `fields` list asserts the list
                // exactly, in order (AF-1) — that is the whole point of the clause.
                if (ea.Count != aa.Count)
                {
                    into.Add(Mismatch.Said(Join(at, "length"), ea.Count.ToString(CultureInfo.InvariantCulture), aa.Count.ToString(CultureInfo.InvariantCulture)));
                    return;
                }

                for (var i = 0; i < ea.Count; i++)
                {
                    Match($"{at}[{i}]", ea[i], aa[i], into);
                }

                return;

            case JsonArray:
                into.Add(new Mismatch(at, expected.DeepClone(), actual?.DeepClone()));
                return;

            default:
                if (!ScalarEquals(expected, actual))
                {
                    into.Add(new Mismatch(at, expected?.DeepClone(), ReferenceEquals(actual, Absent) ? JsonValue.Create("(absent)") : actual?.DeepClone()));
                }

                return;
        }
    }

    /// <summary>
    /// A distinguishable stand-in for a key the observation does not carry. A driver that reported
    /// an absent key as JSON null could not tell "the framework says null" from "the framework has
    /// no such property", and those are the two answers the whole suite is about.
    /// </summary>
    public static readonly JsonNode Absent = JsonValue.Create("(absent)")!;

    /// <summary>Scalars by identity, with JSON's one number type compared numerically.</summary>
    public static bool ScalarEquals(JsonNode? expected, JsonNode? actual)
    {
        if (ReferenceEquals(actual, Absent))
        {
            return false;
        }

        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        var ek = expected.GetValueKind();
        var ak = actual.GetValueKind();
        if (ek == JsonValueKind.Number && ak == JsonValueKind.Number)
        {
            // JSON has one number type; the framework has several, and a fixture's 0.9 reaches the
            // matcher beside a float, an int or a long depending on which record it came off.
            return Math.Abs(AsDouble(expected) - AsDouble(actual)) < 1e-6;
        }

        return ek == ak && ek switch
        {
            JsonValueKind.String => string.Equals(expected.GetValue<string>(), actual.GetValue<string>(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => expected.ToJsonString() == actual.ToJsonString(),
        };
    }

    /// <summary>
    /// A JSON number as a double, whatever CLR numeric type the node happens to hold. A node built
    /// from a <c>float</c> refuses <c>GetValue&lt;double&gt;</c> outright, so every numeric read in
    /// the driver goes through here.
    /// </summary>
    public static double AsDouble(JsonNode node)
    {
        var value = node.AsValue();
        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<float>(out var f))
        {
            return f;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        return value.TryGetValue<decimal>(out var m)
            ? (double)m
            : double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Join(string at, string key) => at.Length == 0 ? key : $"{at}.{key}";
}
