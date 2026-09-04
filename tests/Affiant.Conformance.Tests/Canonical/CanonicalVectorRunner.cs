using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Affiant.Abstractions.Models;
using Affiant.Conformance.Tests.Matching;
using Affiant.Conformance.Tests.Model;

namespace Affiant.Conformance.Tests.Canonical;

/// <summary>
/// Runs the seven canonical byte vectors (<c>RUNNER.md</c> §9) — a different document shape from a
/// fixture, and not run through the step machinery.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a vector row in this run does and does not say.</b> A vector is measured against the
/// driver's own canonicaliser, which is the "second canonicaliser written out from the rule" that
/// <c>RUNNER.md</c> §9 names as one of the three paths that have to agree — never against the
/// framework's own <c>CanonicalSerializer</c>, which would be the implementation grading its own
/// homework. What a vector <b>does</b> say is two things a reader of the parity report needs:
/// </para>
/// <list type="number">
/// <item>whether the rule's text, implemented independently, reproduces the pinned bytes and
/// digest — a disagreement there is a finding about the rule or the vector, not about .NET;</item>
/// <item>whether the shipped <c>Affidavit</c> model can <b>hold</b> the shape the vector pins at
/// all. The properties it is measured against are read off the shipped records themselves, so a
/// row that says "the record has no such property" is a statement about the tree that produced the
/// run and not about whatever release a list in this file was last edited for.</item>
/// </list>
/// </remarks>
internal static class CanonicalVectorRunner
{
    /// <summary>The properties an <c>Affidavit</c> can hold, read off the shipped record.</summary>
    private static readonly HashSet<string> AffidavitProperties = PropertiesOf(typeof(Affidavit));

    /// <summary>The properties an <c>AffidavitField</c> can hold, read off the shipped record.</summary>
    private static readonly HashSet<string> FieldProperties = PropertiesOf(typeof(AffidavitField));

    /// <summary>The properties a <c>ProvenanceTag</c> can hold, read off the shipped record.</summary>
    private static readonly HashSet<string> TagProperties = PropertiesOf(typeof(ProvenanceTag));

    /// <summary>
    /// The JSON property names a shipped record carries, under the naming the canonical form uses:
    /// a <c>[JsonPropertyName]</c> where one is declared, camel case otherwise.
    /// </summary>
    private static HashSet<string> PropertiesOf(Type record) => new(
        record.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property =>
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name)),
        StringComparer.Ordinal);

    /// <summary>Reproduces one vector, reporting every disagreement it found.</summary>
    public static (string Verdict, IReadOnlyList<Mismatch> Diff, string? Reason) Run(CanonicalVector vector)
    {
        var diff = new List<Mismatch>();
        string? reason = null;

        if (vector.Amendments is not null)
        {
            // The sworn form is the Affidavit COMBINED with its accepted amendments, and an amended
            // field's tag names the act that amended it (PV-2). A tag in this release cannot name an
            // act: it has no binding and no timestamp, so the sworn form cannot be built at all.
            reason = "not-implemented: amendment folding. The sworn form an execution grant binds to is the " +
                     "Affidavit combined with its accepted amendments, and an amended field's tag must name the " +
                     "reviewer act that amended it (PV-2, SR-1). This driver builds the vector's input as JSON " +
                     "and has no accepted-amendment path to fold through, so the sworn form is not built here.";
            diff.Add(Mismatch.Said("amendments", "the sworn form, amendments folded in", "the model cannot express an amendment's tag"));
        }

        var canonical = Canonicaliser.Serialize(vector.Input);
        if (canonical != vector.ExpectedBytesUtf8)
        {
            diff.Add(Mismatch.Said("expectedBytesUtf8", Excerpt(vector.ExpectedBytesUtf8, canonical), Excerpt(canonical, vector.ExpectedBytesUtf8)));
        }

        var digest = Canonicaliser.Sha256Hex(canonical);
        if (digest != vector.ExpectedSha256)
        {
            diff.Add(Mismatch.Said("expectedSha256", vector.ExpectedSha256, digest));
        }

        CheckModelCanHold(vector.Input, diff);
        return (diff.Count == 0 ? "pass" : "fail", diff, reason);
    }

    /// <summary>Names every property of the vector's shape the .NET model has nowhere to put.</summary>
    private static void CheckModelCanHold(JsonObject input, List<Mismatch> diff)
    {
        // A vector that is not Affidavit-shaped has no model to be held by, so there is nothing to
        // check. From the rulebook's v0.1.1 all seven are Affidavits — the two that stress key order
        // and number forms carry their cases inside a field's value — so this guard no longer fires;
        // it stays because what a vector may contain is the rulebook's call, not this driver's, and
        // a runner that assumed otherwise would crash on the first vector that changed shape.
        if (input["fields"] is not JsonArray fields || input["operationType"] is null)
        {
            return;
        }

        foreach (var key in input.Select(kv => kv.Key).Where(k => !AffidavitProperties.Contains(k)))
        {
            diff.Add(Mismatch.Said($"model.{key}", "a property of the Affidavit record", "(absent) - the record has no such property"));
        }

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i]!.AsObject();
            foreach (var key in field.Select(kv => kv.Key).Where(k => !FieldProperties.Contains(k)))
            {
                diff.Add(Mismatch.Said($"model.fields[{i}].{key}", "a property of the AffidavitField record", "(absent)"));
            }

            if (field["provenance"] is not JsonObject provenance)
            {
                continue;
            }

            foreach (var (where, tag) in Tags(provenance))
            {
                foreach (var key in tag.Select(kv => kv.Key).Where(k => !TagProperties.Contains(k)))
                {
                    diff.Add(Mismatch.Said($"model.fields[{i}].provenance.{where}.{key}", "a property of the ProvenanceTag record", "(absent)"));
                }
            }
        }
    }

    private static IEnumerable<(string Where, JsonObject Tag)> Tags(JsonObject provenance)
    {
        if (provenance["current"] is JsonObject current)
        {
            yield return ("current", current);
        }

        if (provenance["prior"] is JsonArray prior)
        {
            for (var i = 0; i < prior.Count; i++)
            {
                if (prior[i] is JsonObject tag)
                {
                    yield return ($"prior[{i}]", tag);
                }
            }
        }
    }

    /// <summary>The bytes around the first character where two canonical documents part company.</summary>
    private static string Excerpt(string subject, string other)
    {
        var at = 0;
        while (at < subject.Length && at < other.Length && subject[at] == other[at])
        {
            at++;
        }

        var from = Math.Max(0, at - 24);
        var take = Math.Min(72, subject.Length - from);
        return $"...{subject.Substring(from, take)}... (first difference at byte {at} of {subject.Length})";
    }
}
