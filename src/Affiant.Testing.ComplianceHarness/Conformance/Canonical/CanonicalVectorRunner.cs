using System.Text.Json.Nodes;
using Affiant.Testing.ComplianceHarness.Conformance.Loading;
using Affiant.Testing.ComplianceHarness.Conformance.Matching;
using Affiant.Testing.ComplianceHarness.Conformance.Model;
using Affiant.Core.Serialization;
using Json.Schema;

namespace Affiant.Testing.ComplianceHarness.Conformance.Canonical;

/// <summary>
/// Runs the seven canonical byte vectors (<c>RUNNER.md</c> §9) — a different document shape from a
/// fixture, and not run through the step machinery.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reproduced through the shipped serializer, never re-derived here.</b> The rule is explicit: "a
/// driver reproduces the bytes and the digest; it does not re-derive them", and the three paths that
/// have to agree are the implementation, a second canonicaliser written out from the rule, and an
/// off-the-shelf SHA-256. The second canonicaliser is the rulebook's job and produced the pinned
/// bytes; this driver's job is the first path. Every vector therefore goes through
/// <see cref="CanonicalSerializer"/> — the same exported helper a host calls to mint an execution
/// grant — so a run of this suite says something about the implementation's SR-1 conformance rather
/// than about a canonicaliser written beside the test.
/// </para>
/// <para>
/// <b>The amended vector.</b> Its sworn form is the Affidavit combined with its accepted amendments,
/// which the shipped fold produces: the driver applies it and checks the result against the
/// <c>amendedInput</c> the vector writes down, property for property, before comparing bytes. Two
/// states that differ can only be told apart by reading them, and a byte comparison alone would say
/// that byte 447 differs rather than which property parted company.
/// </para>
/// <para>
/// <b>Validated first.</b> Each vector is held against <c>canonical-vector.schema.json</c> before it
/// runs, exactly as a fixture is held against <c>fixture.schema.json</c>: a malformed vector must be
/// an error, never a pass.
/// </para>
/// </remarks>
internal static class CanonicalVectorRunner
{
    private static readonly JsonSchema Schema = JsonSchema.FromFile(
        Path.Combine(ProtocolSuite.Instance.Root, "canonical-vector.schema.json"));

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = false,
    };

    /// <summary>Reproduces one vector, reporting every disagreement it found.</summary>
    public static (string Verdict, IReadOnlyList<Mismatch> Diff, string? Reason) Run(CanonicalVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        if (SchemaProblems(vector) is { Count: > 0 } problems)
        {
            return (
                "error",
                [Mismatch.Said("document", "a document canonical-vector.schema.json admits", string.Join("; ", problems))],
                $"does not validate against canonical-vector.schema.json: {string.Join("; ", problems)}");
        }

        var diff = new List<Mismatch>();

        // The sworn form: the input as filed, or the input with the reviewer's accepted amendments
        // folded in by the SHIPPED fold (PV-2, DK-2).
        var sworn = vector.Input;
        if (vector.Amendments is { } amendments && vector.ReviewerAct is { } act)
        {
            sworn = CanonicalSerializer.ApplyAmendmentsForCanonical(
                vector.Input,
                Amendments(amendments),
                Guid.Parse(act["entryId"]!.GetValue<string>()),
                DateTimeOffset.Parse(
                    act["decisionAt"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture),
                act["by"]!.GetValue<string>());

            if (vector.AmendedInput is { } expected)
            {
                var produced = CanonicalSerializer.CanonicalString(sworn);
                var stated = CanonicalSerializer.CanonicalString(expected);
                if (!string.Equals(produced, stated, StringComparison.Ordinal))
                {
                    diff.Add(Mismatch.Said(
                        "amendedInput",
                        Excerpt(stated, produced),
                        Excerpt(produced, stated)));
                }
            }
        }

        var canonical = CanonicalSerializer.CanonicalString(sworn);
        if (canonical != vector.ExpectedBytesUtf8)
        {
            diff.Add(Mismatch.Said(
                "expectedBytesUtf8",
                Excerpt(vector.ExpectedBytesUtf8, canonical),
                Excerpt(canonical, vector.ExpectedBytesUtf8)));
        }

        var digest = CanonicalSerializer.CanonicalHash(sworn);
        if (digest != vector.ExpectedSha256)
        {
            diff.Add(Mismatch.Said("expectedSha256", vector.ExpectedSha256, digest));
        }

        return (diff.Count == 0 ? "pass" : "fail", diff, null);
    }

    /// <summary>The vector's amendment map, as the fold takes it.</summary>
    private static Dictionary<string, object?> Amendments(JsonObject amendments)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in amendments)
        {
            map[name] = value is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<object?>(value.ToJsonString());
        }

        return map;
    }

    private static IReadOnlyList<string> SchemaProblems(CanonicalVector vector)
    {
        var document = ProtocolSuite.ReadObject(vector.SourcePath);
        var result = Schema.Evaluate(document, Options);
        if (result.IsValid) return [];

        return
        [
            .. Flatten(result)
                .Where(d => !d.IsValid && d.Errors is { Count: > 0 })
                .Select(d => $"{(string.IsNullOrEmpty(d.InstanceLocation.ToString()) ? "(root)" : d.InstanceLocation.ToString())}: {string.Join("; ", d.Errors!.Values)}")
                .Distinct(StringComparer.Ordinal)
                .Take(8),
        ];
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;
        foreach (var child in results.Details.SelectMany(Flatten))
        {
            yield return child;
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
