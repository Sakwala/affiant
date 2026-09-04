using System.Reflection;
using System.Text.Json.Nodes;
using Affiant.Conformance.Tests.Loading;
using Affiant.Conformance.Tests.Reporting;
using Json.Schema;
using Xunit;
using Xunit.Abstractions;

namespace Affiant.Conformance.Tests;

/// <summary>
/// The .NET conformance driver's assertions.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests do not assert that every fixture passes.</b> The suite is run against a release
/// with published, named gaps, and the thing CI enforces is that the set of fixtures that fail is
/// <b>exactly</b> the set the parity manifest declares — a fixture that starts failing and a
/// fixture that starts passing both fail the build, because a check that caught only the first
/// would let a fix rot unrecorded and the manifest would become a document nobody trusts
/// (<c>PARITY.md</c>).
/// </para>
/// <para>
/// The suite itself runs once per process; every assertion below reads the same run.
/// </para>
/// </remarks>
public sealed class ConformanceDriverTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryFixtureTheIndexListsWasRun()
    {
        var run = ConformanceRun.Instance;
        var expected = ProtocolSuite.Instance.Manifest.Select(m => m.Id).ToArray();
        var actual = run.Results.Select(r => r.Id).ToArray();

        // A driver runs every fixture the manifest lists. Running a subset and reporting a pass is
        // the failure mode the whole arrangement exists to prevent.
        Assert.Equal(expected, actual);

        var summary = run.Document["summary"]!;
        output.WriteLine(
            $"conformance {ConformanceRun.ImplementationName}@{ConformanceRun.ImplementationVersion} " +
            $"against protocol {ProtocolSuite.ProtocolTag}: " +
            $"{summary["passed"]} passed, {summary["failed"]} failed, {summary["errored"]} errored, " +
            $"{summary["skipped"]} skipped of {summary["total"]}.");
        if (run.WrittenTo is { } path)
        {
            output.WriteLine($"run log: {path}");
        }
    }

    [Fact]
    public void NoFixtureIsSilentlySkipped()
    {
        // A skip is legitimate only where the parity manifest declares one, and this driver declares
        // none: a port it cannot supply is an error, not a skip, and it counts against the
        // implementation exactly like a failure.
        Assert.DoesNotContain(ConformanceRun.Instance.Results, r => r.Outcome == "skipped");
    }

    [Fact]
    public void TheResultDocumentValidatesAgainstItsSchema()
    {
        var schema = JsonSchema.FromFile(Path.Combine(ProtocolSuite.Instance.Root, "results.schema.json"));
        var result = schema.Evaluate(
            ConformanceRun.Instance.Document,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = false });

        Assert.True(result.IsValid, Explain(result));
    }

    [Fact]
    public void TheFailingSetEqualsTheParityManifest()
    {
        var manifest = ParityManifest.Load();
        Assert.NotNull(manifest);

        var declared = manifest!.FailingIds;
        var observed = ConformanceRun.Instance.FailingIds;

        var regressed = observed.Except(declared, StringComparer.Ordinal).ToArray();
        var closed = declared.Except(observed, StringComparer.Ordinal).ToArray();

        foreach (var id in regressed)
        {
            output.WriteLine($"FAILING, NOT DECLARED: {id}");
        }

        foreach (var id in closed)
        {
            output.WriteLine($"DECLARED, NOW PASSING: {id}");
        }

        Assert.True(
            regressed.Length == 0 && closed.Length == 0,
            $"The failing set and {ParityManifest.RelativePath} disagree. " +
            $"Failing but not declared: {Join(regressed)}. Declared but now passing: {Join(closed)}. " +
            "The manifest is a published claim about this implementation: regenerate it, read the diff, " +
            "and put the change in the pull request.");
    }

    [Fact]
    public void TheParityManifestDeclaresADispositionForEveryGap()
    {
        var manifest = ParityManifest.Load();
        Assert.NotNull(manifest);

        // A failure with no disposition is a failure nobody has looked at, which is why the format
        // has no way to express one.
        var undisposed = manifest!.Rows
            .Where(r => r["disposition"]?.GetValue<string>() is not ("fixed" or "fenced" or "ignored"))
            .Select(r => r["id"]!.GetValue<string>())
            .ToArray();

        Assert.True(undisposed.Length == 0, $"No disposition on: {Join(undisposed)}");
    }

    [Fact]
    public void TheParityManifestValidatesAgainstItsSchema()
    {
        var manifest = ParityManifest.Load();
        Assert.NotNull(manifest);

        var schema = JsonSchema.FromFile(Path.Combine(ProtocolSuite.Instance.Root, "parity", "MANIFEST.schema.json"));
        var result = schema.Evaluate(
            manifest!.Document,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = false });

        Assert.True(result.IsValid, Explain(result));
    }

    [Fact]
    public void TheParityManifestInheritsEveryRulebookExemption()
    {
        var manifest = ParityManifest.Load();
        Assert.NotNull(manifest);

        // An implementation may not invent an exemption: exempting yourself from a rule is not a
        // parity report, it is a press release. The entries are COPIED from the rulebook's own list,
        // and this checks the copy is complete and carries nothing extra.
        var rulebook = ProtocolSuite.ReadObject(Path.Combine(ProtocolSuite.Instance.Root, "lint", "coverage-exemptions.json"))
            ["exemptions"]!.AsArray().Select(e => e!["rule"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
        var declared = manifest!.Document["exemptions"]!.AsArray()
            .Select(e => e!["rule"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(rulebook, declared);
    }

    [Fact]
    public void EveryOracleFixtureFailsOnThisRelease()
    {
        // A fixture whose rule a known defective release violates is accepted into the suite only if
        // it FAILS against that release. A listed fixture that passes here is not good news: it means
        // the fixture is mis-authored or the recorded defect is not what it was said to be, and it is
        // investigated before the tag is cut. It is never tuned into failing.
        var outcomes = ConformanceRun.Instance.Results.ToDictionary(r => r.Id, r => r.Outcome, StringComparer.Ordinal);
        var passing = ProtocolSuite.Instance.Manifest
            .Where(m => m.Oracle is not null && m.Oracle.MustFailOn.Contains($"dotnet@{ConformanceRun.ImplementationVersion}"))
            .Where(m => outcomes.GetValueOrDefault(m.Id) == "pass")
            .Select(m => $"{m.Id} (defect recorded: {m.Oracle!.Defect})")
            .ToArray();

        Assert.True(
            passing.Length == 0,
            "Negative-oracle fixtures PASSED on a release recorded as violating their rule: " +
            string.Join("; ", passing) +
            ". Investigate the fixture and the recorded defect; do not change the fixture to make it fail.");
    }

    [Fact]
    public void TheVendoredSuiteIsTheDocumentThePinNames()
    {
        var root = ProtocolSuite.Instance.Root;
        var sums = Path.Combine(root, "SHA256SUMS");
        Assert.True(File.Exists(sums), $"No SHA256SUMS beside the vendored suite at {root}. Run conformance/sync.sh.");

        var declared = File.ReadAllLines(sums)
            .Where(line => line.Length > 0)
            .ToDictionary(
                line => line[(line.IndexOf("  ", StringComparison.Ordinal) + 2)..],
                line => line[..line.IndexOf("  ", StringComparison.Ordinal)],
                StringComparer.Ordinal);

        var drifted = new List<string>();
        foreach (var (relative, expected) in declared)
        {
            var path = Path.Combine(root, relative.Replace("./", string.Empty, StringComparison.Ordinal));
            if (!File.Exists(path))
            {
                drifted.Add($"{relative} (missing)");
                continue;
            }

            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            if (actual != expected)
            {
                drifted.Add(relative);
            }
        }

        Assert.True(
            drifted.Count == 0,
            "The vendored conformance suite has been edited: " + Join(drifted) +
            ". An edited fixture is no longer the document the comparison is about.");
    }

    private static string Join(IEnumerable<string> ids)
    {
        var list = ids.ToArray();
        return list.Length == 0 ? "(none)" : string.Join(", ", list);
    }

    private static string Explain(EvaluationResults results) =>
        string.Join(
            Environment.NewLine,
            Flatten(results)
                .Where(d => !d.IsValid && d.Errors is { Count: > 0 })
                .Select(d => $"{d.InstanceLocation}: {string.Join("; ", d.Errors!.Values)}")
                .Distinct(StringComparer.Ordinal)
                .Take(10));

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;
        foreach (var child in results.Details.SelectMany(Flatten))
        {
            yield return child;
        }
    }
}

/// <summary>This implementation's published statement of exactly which fixtures it does not pass.</summary>
internal sealed class ParityManifest(JsonObject document)
{
    /// <summary>Where the manifest lives in this repository.</summary>
    public const string RelativePath = "conformance/parity/dotnet-v0.1.json";

    /// <summary>The manifest as it stands on disk.</summary>
    public JsonObject Document => document;

    /// <summary>Every failing row, in the order the manifest lists them.</summary>
    public IReadOnlyList<JsonObject> Rows => document["failing"]!.AsArray().Select(r => r!.AsObject()).ToArray();

    /// <summary>The ids the manifest declares as failing. Exactly these and no others.</summary>
    public IReadOnlyList<string> FailingIds => Rows.Select(r => r["id"]!.GetValue<string>()).ToArray();

    /// <summary>Reads the manifest from the repository, or null when there is not one yet.</summary>
    public static ParityManifest? Load()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                return new ParityManifest(ProtocolSuite.ReadObject(path));
            }

            directory = directory.Parent;
        }

        return null;
    }
}
