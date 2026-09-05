using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Testing.ComplianceHarness.Conformance.Canonical;
using Affiant.Testing.ComplianceHarness.Conformance.Execution;
using Affiant.Testing.ComplianceHarness.Conformance.Loading;
using Affiant.Testing.ComplianceHarness.Conformance.Matching;
using Affiant.Testing.ComplianceHarness.Conformance.Model;

namespace Affiant.Testing.ComplianceHarness.Conformance.Reporting;

/// <summary>One fixture's row in the result document.</summary>
internal sealed record FixtureResult(string Id, string Outcome, IReadOnlyList<Mismatch> Diff, double DurationMs, string? Reason);

/// <summary>
/// The run: every document the index lists, executed once, and the result document that is the
/// evidence behind the parity manifest's claim.
/// </summary>
/// <remarks>
/// The suite runs once per process and is shared by every assertion in the project, because running
/// 63 documents per test would say the same thing five times over and take five times as long.
/// </remarks>
internal sealed class ConformanceRun
{
    /// <summary>
    /// The version of the framework this run exercised, read off the packages it is bound to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived, not declared. A constant here says what somebody last remembered to type; the
    /// assembly's own informational version says what the tree actually builds, so the run log is
    /// named after the thing that was measured and the parity manifest's <c>version</c> is a fact
    /// about it. A build-metadata suffix (<c>+&lt;commit&gt;</c>) is stripped: the commit is
    /// recorded separately, and it is not part of the release the manifest is a claim about.
    /// </para>
    /// <para>
    /// It reads <c>Affiant.Core</c> rather than this test assembly: the driver's own version is
    /// nobody's concern, and the packages are what a reader installs.
    /// </para>
    /// </remarks>
    public static readonly string ImplementationVersion = ReadImplementationVersion();

    /// <summary>The implementation's identifier, matching the parity manifest's.</summary>
    public const string ImplementationName = "dotnet";

    private ConformanceRun(IReadOnlyList<FixtureResult> results, JsonObject document, string? writtenTo)
    {
        Results = results;
        Document = document;
        WrittenTo = writtenTo;
    }

    /// <summary>One entry per fixture the index lists, including the ones that passed.</summary>
    public IReadOnlyList<FixtureResult> Results { get; }

    /// <summary>The result document, valid against <c>results.schema.json</c>.</summary>
    public JsonObject Document { get; }

    /// <summary>Where the document was written, when a repository was found to write it into.</summary>
    public string? WrittenTo { get; }

    /// <summary>The ids a parity manifest must list: every fixture that failed or errored.</summary>
    public IReadOnlyList<string> FailingIds =>
        Results.Where(r => r.Outcome is "fail" or "error").Select(r => r.Id).ToArray();

    /// <summary>
    /// Runs the suite once, against the rulebook at <paramref name="protocolRoot"/> (null: the copy
    /// beside the running assembly), writing the run into <paramref name="writeRunTo"/> (null: the
    /// repository's own conformance/results, when this is running inside one).
    /// </summary>
    public static ConformanceRun Execute(string protocolRoot, string? writeRunTo)
    {
        var suite = ProtocolSuite.At(protocolRoot);
        var results = new List<FixtureResult>();
        var wall = Stopwatch.StartNew();

        foreach (var entry in suite.Manifest)
        {
            results.Add(RunOne(suite, entry));
        }

        wall.Stop();

        var document = Compose(suite, results, wall.Elapsed.TotalMilliseconds);
        var writtenTo = writeRunTo is { Length: > 0 } ? Write(document, writeRunTo) : null;
        return new ConformanceRun(results, document, writtenTo);
    }

    private static FixtureResult RunOne(ProtocolSuite suite, ManifestEntry entry)
    {
        var timer = Stopwatch.StartNew();
        var path = suite.FixturePath(entry);
        try
        {
            if (entry.Set == "canonical")
            {
                var (verdict, diff, reason) = CanonicalVectorRunner.Run(suite, FixtureLoader.LoadVector(path));
                return new FixtureResult(entry.Id, verdict, diff, timer.Elapsed.TotalMilliseconds, reason);
            }

            var fixture = FixtureLoader.Load(suite, path);
            var outcome = FixtureRunner.RunAsync(fixture, CancellationToken.None).GetAwaiter().GetResult();
            return new FixtureResult(entry.Id, outcome.Verdict, outcome.Diff, timer.Elapsed.TotalMilliseconds, outcome.Reason);
        }
        catch (FixtureDocumentException document)
        {
            // A document the format refuses is not run at all: running it would report a pass, and a
            // pass is the one answer it must never give.
            return new FixtureResult(
                entry.Id, "error", [Mismatch.Said("document", "a fixture the format accepts", document.Message)],
                timer.Elapsed.TotalMilliseconds, document.Message);
        }
        catch (Exception exception)
        {
            // An error is not a pass and not a silent skip, and it counts against the implementation
            // exactly like a failure (RUNNER.md §8).
            return new FixtureResult(
                entry.Id, "error", [Mismatch.Said("driver", "the fixture runs", $"{exception.GetType().Name}: {exception.Message}")],
                timer.Elapsed.TotalMilliseconds, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static JsonObject Compose(
        ProtocolSuite suite, IReadOnlyList<FixtureResult> results, double durationMs)
    {
        var rows = new JsonArray();
        foreach (var result in results)
        {
            var row = new JsonObject
            {
                ["id"] = result.Id,
                ["outcome"] = result.Outcome,
                ["durationMs"] = Math.Round(result.DurationMs, 3),
            };

            if (result.Diff.Count > 0)
            {
                var diff = new JsonArray();
                foreach (var mismatch in result.Diff)
                {
                    diff.Add(new JsonObject
                    {
                        ["at"] = mismatch.At,
                        ["expected"] = mismatch.Expected?.DeepClone(),
                        ["actual"] = mismatch.Actual?.DeepClone(),
                    });
                }

                row["diff"] = diff;
            }

            if (result.Reason is { } reason)
            {
                row["reason"] = reason;
            }

            rows.Add(row);
        }

        return new JsonObject
        {
            ["schemaVersion"] = "0.1.0",
            ["implementation"] = new JsonObject
            {
                ["name"] = ImplementationName,
                ["version"] = ImplementationVersion,
                ["commit"] = Commit(),
                ["runtime"] = "net10.0",
            },
            ["protocolTag"] = suite.ProtocolTag,
            ["producedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["summary"] = new JsonObject
            {
                ["total"] = results.Count,
                ["passed"] = results.Count(r => r.Outcome == "pass"),
                ["failed"] = results.Count(r => r.Outcome == "fail"),
                ["errored"] = results.Count(r => r.Outcome == "error"),
                ["skipped"] = results.Count(r => r.Outcome == "skipped"),
                ["durationMs"] = Math.Round(durationMs, 3),
            },
            ["results"] = rows,
        };
    }

    /// <summary>The informational version of the shipped core, without its build metadata.</summary>
    private static string ReadImplementationVersion()
    {
        var informational = typeof(Affiant.Core.Services.ReviewGate).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            throw new InvalidOperationException(
                "Affiant.Core carries no AssemblyInformationalVersionAttribute, so this run cannot "
                + "say which version of the framework it measured. A parity manifest that named the "
                + "wrong version would be a claim about a release nobody built.");
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }

    /// <summary>
    /// The git commit of the tree this run measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read off <c>Affiant.Core</c>'s own informational version, whose build-metadata suffix the SDK
    /// fills with the source revision — the same assembly the version comes from, so the log's two
    /// facts about the thing measured cannot come from different builds. <c>GITHUB_SHA</c> wins
    /// where CI sets it, for a build that produced the assembly outside a checkout.
    /// </para>
    /// <para>
    /// It falls back to <c>"unknown"</c> and NEVER to the version. The published record's whole
    /// purpose is to name the tree it measured; a version string in this field reads like a commit
    /// and is not one, which is worse than admitting the build did not know.
    /// </para>
    /// </remarks>
    private static string Commit()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: > 0 } fromCi)
        {
            return fromCi;
        }

        var informational = typeof(Affiant.Core.Services.ReviewGate).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var plus = informational?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        return plus >= 0 ? informational![(plus + 1)..] : "unknown";
    }

    /// <summary>
    /// Writes the run into the directory the caller named, as
    /// <c>&lt;implementation&gt;-&lt;version&gt;.json</c>.
    /// </summary>
    /// <remarks>
    /// Only where the caller asked. A package that went looking for somewhere to write would be
    /// writing into a tree it was never given, and a run that only printed to a terminal could tell
    /// a reader that something failed without producing the list a parity manifest is derived from —
    /// so the choice is the caller's, and it is one argument.
    /// </remarks>
    private static string Write(JsonObject document, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{ImplementationName}-{ImplementationVersion}.json");
        File.WriteAllText(
            path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return path;
    }

}
