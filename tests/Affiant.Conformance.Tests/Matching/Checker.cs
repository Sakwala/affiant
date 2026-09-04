using System.Text.Json.Nodes;

namespace Affiant.Conformance.Tests.Matching;

/// <summary>
/// Dispatches the clauses of an <c>expect</c> to their comparisons. Most are the partial object
/// match of <see cref="Matcher"/>; four are not, and each of those has a rule of its own.
/// </summary>
internal static class Checker
{
    /// <summary>Compares every clause a fixture states against the observation, appending every failure found.</summary>
    public static void Check(JsonObject expect, JsonObject observation, IReadOnlySet<string> telemetry, List<Mismatch> into)
    {
        foreach (var (clause, expected) in expect)
        {
            switch (clause)
            {
                case "telemetry":
                    // A membership test, not an equality: the keys must have been emitted at some
                    // point, and other keys may have been emitted too.
                    foreach (var key in Keys(expected))
                    {
                        if (!telemetry.Contains(key))
                        {
                            into.Add(Mismatch.Said($"telemetry[{key}]", "emitted", "never emitted"));
                        }
                    }

                    break;

                case "telemetryAbsent":
                    foreach (var key in Keys(expected))
                    {
                        if (telemetry.Contains(key))
                        {
                            into.Add(Mismatch.Said($"telemetryAbsent[{key}]", "never emitted", "emitted"));
                        }
                    }

                    break;

                case "card" when expected is JsonObject card:
                    CheckCard(card, observation["card"], into);
                    break;

                case "entry" or "superseded" when expected is JsonObject row:
                    CheckRow(clause, row, observation[clause], into);
                    break;

                default:
                    Matcher.Match(clause, expected, Read(observation, clause), into);
                    break;
            }
        }
    }

    private static void CheckRow(string clause, JsonObject expected, JsonNode? actual, List<Mismatch> into)
    {
        if (actual is not JsonObject row)
        {
            into.Add(Mismatch.Said(clause, "a Docket row", "no row was filed or acted on"));
            return;
        }

        foreach (var (key, value) in expected)
        {
            if (key == "lineage" && value is JsonObject lineage)
            {
                CheckLineage($"{clause}.lineage", lineage, row["lineage"], into);
                continue;
            }

            Matcher.Match($"{clause}.{key}", value, Read(row, key), into);
        }
    }

    /// <summary>
    /// A lineage link may carry the sentinel <c>"@some"</c>, which asserts only that the link is
    /// present. An entry id is derived from the proposal, so a fixture cannot state one; what the
    /// rule is about is that a resubmission names what it replaces and the replaced row names it
    /// back (DK-1).
    /// </summary>
    private static void CheckLineage(string at, JsonObject expected, JsonNode? actual, List<Mismatch> into)
    {
        var links = actual as JsonObject;
        foreach (var (key, value) in expected)
        {
            var observed = links is null ? Matcher.Absent : Read(links, key);
            if (value is not null && value.GetValueKind() == System.Text.Json.JsonValueKind.String
                && value.GetValue<string>() == Matcher.SomeSentinel)
            {
                var present = observed is not null
                    && !ReferenceEquals(observed, Matcher.Absent)
                    && observed.GetValueKind() != System.Text.Json.JsonValueKind.Null;
                if (!present)
                {
                    into.Add(Mismatch.Said($"{at}.{key}", "a link to some entry", "no link"));
                }

                continue;
            }

            Matcher.Match($"{at}.{key}", value, observed, into);
        }
    }

    private static void CheckCard(JsonObject expected, JsonNode? actual, List<Mismatch> into)
    {
        if (actual is not JsonObject card)
        {
            into.Add(Mismatch.Said("card", "an Evidence Card", "no card was broadcast"));
            return;
        }

        foreach (var (key, value) in expected)
        {
            if (key == "warningsContain")
            {
                // A list of SUBSTRINGS, each of which must appear somewhere in the card's warnings.
                var warnings = (card["warnings"] as JsonArray)?.Select(w => w!.GetValue<string>()).ToArray() ?? [];
                foreach (var fragment in Keys(value))
                {
                    if (!warnings.Any(w => w.Contains(fragment, StringComparison.Ordinal)))
                    {
                        into.Add(Mismatch.Said(
                            $"card.warningsContain[{fragment}]",
                            "a warning containing it",
                            warnings.Length == 0 ? "the card carries no warnings" : string.Join(" / ", warnings)));
                    }
                }

                continue;
            }

            Matcher.Match($"card.{key}", value, Read(card, key), into);
        }
    }

    private static IEnumerable<string> Keys(JsonNode? node) =>
        (node as JsonArray)?.Select(k => k!.GetValue<string>()) ?? [];

    private static JsonNode? Read(JsonObject observation, string key) =>
        observation.TryGetPropertyValue(key, out var value) ? value : Matcher.Absent;
}
