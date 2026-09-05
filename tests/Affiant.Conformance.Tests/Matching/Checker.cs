using System.Text.Json.Nodes;

namespace Affiant.Conformance.Tests.Matching;

/// <summary>
/// Dispatches the clauses of an <c>expect</c> to their comparisons. Most are the partial object
/// match of <see cref="Matcher"/>; four are not, and each of those has a rule of its own.
/// </summary>
internal static class Checker
{
    /// <summary>Compares every clause a fixture states against the observation, appending every failure found.</summary>
    /// <remarks>
    /// <c>RUNNER.md</c> §5.3: an <c>expect</c> with no <c>error</c> key, or one holding <c>null</c>,
    /// asserts that the step under test produced NO refusal — a positive statement, not the absence
    /// of one. A driver that compared the clause only where a fixture wrote it would let a gate that
    /// refused every act keep most of the suite green, because most fixtures are about what an act
    /// DID and say nothing about it failing.
    /// </remarks>
    public static void Check(JsonObject expect, JsonObject observation, IReadOnlySet<string> telemetry, List<Mismatch> into)
    {
        if (!expect.ContainsKey("error") || expect["error"] is null)
        {
            CheckNoRefusal(observation["error"], into);
        }

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

                case "error" when expected is JsonObject error:
                    CheckError(error, observation["error"], into);
                    break;

                default:
                    Matcher.Match(clause, expected, Read(observation, clause), into);
                    break;
            }
        }
    }

    /// <summary>
    /// The step under test succeeded: it produced no refusal (<c>RUNNER.md</c> §5.3).
    /// </summary>
    private static void CheckNoRefusal(JsonNode? observed, List<Mismatch> into)
    {
        if (observed is null) return;

        into.Add(Mismatch.Said(
            "error",
            "no refusal: the fixture states none, which asserts the act succeeded",
            observed.ToJsonString()));
    }

    /// <summary>
    /// The refusal the step under test produced. <c>code</c> is compared as a string, exactly;
    /// <c>messageContains</c> must appear as a <b>substring</b> of the refusal's reason
    /// (<c>RUNNER.md</c> §5.3).
    /// </summary>
    /// <remarks>
    /// A rule the fixture pins in prose — "a host reports once", "AZ-3" — is pinned so that a caller
    /// learns why from what it is handed back, not from a log line somebody has to go and find. A
    /// driver comparing <c>messageContains</c> as though it were a property would report it absent
    /// on every refusal, however good the reason.
    /// </remarks>
    private static void CheckError(JsonObject expected, JsonNode? actual, List<Mismatch> into)
    {
        if (actual is not JsonObject refusal)
        {
            into.Add(Mismatch.Said("error", expected.ToJsonString(), "no refusal was produced"));
            return;
        }

        foreach (var (key, value) in expected)
        {
            if (key != "messageContains")
            {
                Matcher.Match($"error.{key}", value, Read(refusal, key), into);
                continue;
            }

            var needle = value?.GetValue<string>() ?? string.Empty;
            var reason = refusal["message"]?.GetValue<string>() ?? string.Empty;
            if (!reason.Contains(needle, StringComparison.Ordinal))
            {
                into.Add(Mismatch.Said(
                    "error.messageContains",
                    needle,
                    reason.Length == 0 ? "the refusal carries no reason" : reason));
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
