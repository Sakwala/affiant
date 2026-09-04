using System.Text.Json.Nodes;
using Affiant.Conformance.Tests.Matching;
using Affiant.Conformance.Tests.Model;

namespace Affiant.Conformance.Tests.Canonical;

/// <summary>
/// Runs the seven canonical byte vectors (<c>RUNNER.md</c> §9) — a different document shape from a
/// fixture, and not run through the step machinery.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a vector row in this run does and does not say.</b> <c>1.0.0-beta.1</c> exports no
/// canonical-hash helper, so no vector here is a statement that the framework passes SR-1; every
/// one of them is measured against the driver's own canonicaliser, which is the "second
/// canonicaliser written out from the rule" that <c>RUNNER.md</c> §9 names as one of the three
/// paths that have to agree. What a vector <b>does</b> say is two things a reader of the parity
/// report needs:
/// </para>
/// <list type="number">
/// <item>whether the rule's text, implemented independently, reproduces the pinned bytes and
/// digest — a disagreement there is a finding about the rule or the vector, not about .NET;</item>
/// <item>whether the .NET <c>Affidavit</c> model can <b>hold</b> the shape the vector pins at all.
/// It cannot: the record has no <c>protocolVersion</c>, no <c>populatedConfidence</c>, no
/// <c>emptyFieldCount</c>, no <c>conversationTurn</c> and no <c>createdAt</c>, and a provenance tag
/// has no <c>note</c>, no <c>at</c> and no <c>binding</c> (a tag is source, confidence, evidence and
/// conversation turn, and nothing else). A vector whose form needs any of those fails, and the diff
/// names the property.</item>
/// </list>
/// </remarks>
internal static class CanonicalVectorRunner
{
    /// <summary>The properties an <c>Affidavit</c> can hold in this release.</summary>
    private static readonly HashSet<string> AffidavitProperties = new(StringComparer.Ordinal)
    {
        "operationType", "entityType", "entityId", "fields", "aggregateConfidence", "warnings", "requiresConfirmation",
    };

    /// <summary>The properties an <c>AffidavitField</c> can hold in this release.</summary>
    private static readonly HashSet<string> FieldProperties = new(StringComparer.Ordinal)
    {
        "name", "value", "previousValue", "provenance", "isMandatory", "kind", "allowedValues", "pattern",
    };

    /// <summary>The properties a <c>ProvenanceTag</c> can hold in this release.</summary>
    private static readonly HashSet<string> TagProperties = new(StringComparer.Ordinal)
    {
        "source", "confidence", "evidence", "conversationTurn",
    };

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
                     "reviewer act that amended it (PV-2, SR-1). A ProvenanceTag in 1.0.0-beta.1 carries a source, " +
                     "a confidence, an evidence string and a conversation turn, and nothing that can name an act.";
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
