using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Conformance.Tests.Canonical;
using Affiant.Conformance.Tests.Execution;
using Affiant.Conformance.Tests.Loading;
using Affiant.Conformance.Tests.Matching;
using Affiant.Conformance.Tests.Model;

namespace Affiant.Conformance.Tests.Reporting;

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
    /// <summary>The version of the framework this run exercised.</summary>
    public const string ImplementationVersion = "1.0.0-beta.1";

    /// <summary>The implementation's identifier, matching the parity manifest's.</summary>
    public const string ImplementationName = "dotnet";

    private static readonly Lazy<ConformanceRun> Lazy = new(Execute, LazyThreadSafetyMode.ExecutionAndPublication);

    private ConformanceRun(IReadOnlyList<FixtureResult> results, JsonObject document, string? writtenTo)
    {
        Results = results;
        Document = document;
        WrittenTo = writtenTo;
    }

    /// <summary>The one run for this process.</summary>
    public static ConformanceRun Instance => Lazy.Value;

    /// <summary>One entry per fixture the index lists, including the ones that passed.</summary>
    public IReadOnlyList<FixtureResult> Results { get; }

    /// <summary>The result document, valid against <c>results.schema.json</c>.</summary>
    public JsonObject Document { get; }

    /// <summary>Where the document was written, when a repository was found to write it into.</summary>
    public string? WrittenTo { get; }

    /// <summary>The ids a parity manifest must list: every fixture that failed or errored.</summary>
    public IReadOnlyList<string> FailingIds =>
        Results.Where(r => r.Outcome is "fail" or "error").Select(r => r.Id).ToArray();

    private static ConformanceRun Execute()
    {
        var suite = ProtocolSuite.Instance;
        var results = new List<FixtureResult>();
        var wall = Stopwatch.StartNew();

        foreach (var entry in suite.Manifest)
        {
            results.Add(RunOne(suite, entry));
        }

        wall.Stop();

        var document = Compose(results, wall.Elapsed.TotalMilliseconds);
        var writtenTo = TryWrite(document);
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
                var (verdict, diff, reason) = CanonicalVectorRunner.Run(FixtureLoader.LoadVector(path));
                return new FixtureResult(entry.Id, verdict, diff, timer.Elapsed.TotalMilliseconds, reason);
            }

            var fixture = FixtureLoader.Load(path);
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

    private static JsonObject Compose(IReadOnlyList<FixtureResult> results, double durationMs)
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
            ["protocolTag"] = ProtocolSuite.ProtocolTag,
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

    private static string Commit() =>
        Environment.GetEnvironmentVariable("GITHUB_SHA")
        ?? Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "CommitHash")?.Value
        ?? ImplementationVersion;

    /// <summary>
    /// Writes the run beside the parity manifest it is the evidence for. The manifest is the claim;
    /// the log is the evidence, and a run that only printed to a terminal could tell a reader that
    /// something failed without producing the list the manifest is derived from.
    /// </summary>
    private static string? TryWrite(JsonObject document)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null)
        {
            var conformance = Path.Combine(directory.FullName, "conformance");
            if (File.Exists(Path.Combine(conformance, "PROTOCOL_PIN")))
            {
                var results = Path.Combine(conformance, "results");
                Directory.CreateDirectory(results);
                var path = Path.Combine(results, $"{ImplementationName}-{ImplementationVersion}.json");
                File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
                return path;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
