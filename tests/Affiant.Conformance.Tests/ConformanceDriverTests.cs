using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Affiant.Abstractions.Interfaces;
using System.Text.Json.Nodes;
using Affiant.Testing.ComplianceHarness.Conformance.Loading;
using Affiant.Testing.ComplianceHarness.Conformance.Reporting;
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
    /// <summary>
    /// The framework's own vendored rulebook, by absolute path.
    /// </summary>
    /// <remarks>
    /// Stated, not found: the harness reads every file a run needs from the root it is given, and
    /// has no ambient copy to fall back on. This project's build copies <c>protocol/</c> beside the
    /// test assembly, and this is the sentence that says so — a consumer's would name its own tree.
    /// </remarks>
    private static readonly string ProtocolRoot =
        Path.Combine(Path.GetDirectoryName(typeof(ConformanceDriverTests).Assembly.Location)!, "protocol");

    /// <summary>The vendored rulebook, loaded once per process.</summary>
    private static ProtocolSuite Suite => LazySuite.Value;

    private static readonly Lazy<ProtocolSuite> LazySuite =
        new(() => ProtocolSuite.At(ProtocolRoot), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The one run for this process. Running 63 documents per assertion would say the same thing
    /// several times over and take several times as long.
    /// </summary>
    private static ConformanceRun Run => LazyRun.Value;

    private static readonly Lazy<ConformanceRun> LazyRun =
        new(() => ConformanceRun.Execute(ProtocolRoot, RepositoryResults()),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Where this repository keeps its run logs — the evidence the parity manifest rests on.
    /// </summary>
    /// <remarks>
    /// The harness writes where a caller says and nowhere else, so finding this repository is this
    /// project's job: walk up from the assembly for the <c>conformance/</c> directory the pin lives
    /// in. Null outside a checkout (a packaged run), where the run is returned and not written.
    /// </remarks>
    private static string? RepositoryResults()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(ConformanceDriverTests).Assembly.Location)!);
        while (directory is not null)
        {
            var conformance = Path.Combine(directory.FullName, "conformance");
            if (File.Exists(Path.Combine(conformance, "PROTOCOL_PIN")))
            {
                return Path.Combine(conformance, "results");
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// AZ-7 and GT-6 together: the compliance harness arms a tripwire executor so a fixture can
    /// assert the gate never reached it, and that tripwire is the ONLY <c>IWriteExecutor</c> in any
    /// of the ten shipped assemblies — and it throws. An executor in a shipped package that did
    /// anything else would be the path AZ-7 says does not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All ten, loaded on purpose.</b> An enumeration of the assemblies that happen to be loaded
    /// asserts over whatever this test project referenced — five of the ten — and says nothing about
    /// the other five, which is exactly the shape of claim that reads as covering everything and
    /// covers half. The list below is asserted equal to the solution's packable projects, so a new
    /// package cannot join the release without joining this check.
    /// </para>
    /// <para>
    /// The public-API analyzer is a second guard — RS0016 fails the build on an undeclared public
    /// type, so a public executor implementation could not be added silently — and the source scan
    /// in <c>Affiant.Core.Tests.Gate.ExecutorReachabilityTests</c> is a third. This test relies on
    /// neither: it reads all ten assemblies' own metadata and looks.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOnlyExecutorInAnyShippedAssembly_IsATripwireThatThrows()
    {
        var implementations = ShippedAssemblies()
            .SelectMany(ExecutorImplementationsIn)
            .ToArray();

        var tripwire = Assert.Single(implementations);
        Assert.StartsWith(
            "Affiant.Testing.ComplianceHarness.Conformance.", tripwire.TypeName, StringComparison.Ordinal);

        // And it is a tripwire: the type is loaded — this project references the harness — and asked
        // to execute. It must refuse to be an executor.
        var type = typeof(Affiant.Testing.ComplianceHarness.ConformanceSuite).Assembly
            .GetType(tripwire.TypeName, throwOnError: true)!;
        var instance = (IWriteExecutor)Activator.CreateInstance(type, nonPublic: true)!;

        Assert.ThrowsAny<Exception>(() => instance
            .ExecuteAsync(null!, null, CancellationToken.None)
            .GetAwaiter()
            .GetResult());
    }

    /// <summary>
    /// Every type in <paramref name="assembly"/> that implements <c>IWriteExecutor</c>, read from
    /// the assembly's own metadata.
    /// </summary>
    /// <remarks>
    /// Metadata rather than reflection: loading an adapter assembly for a look would drag in every
    /// dependency it declares — Entity Framework, SignalR, the two agent SDKs — and a type that
    /// failed to load for want of one would silently drop out of the answer. This reads what the
    /// assembly says about itself, so all ten are examined whatever is installed.
    /// </remarks>
    private static IEnumerable<(string Assembly, string TypeName)> ExecutorImplementationsIn(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var found = new List<(string, string)>();

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);

            foreach (var implementation in type.GetInterfaceImplementations()
                         .Select(reader.GetInterfaceImplementation))
            {
                if (NameOf(reader, implementation.Interface) != nameof(IWriteExecutor)) continue;

                var ns = reader.GetString(type.Namespace);
                var name = reader.GetString(type.Name);
                found.Add((Path.GetFileNameWithoutExtension(assemblyPath), ns.Length == 0 ? name : ns + "." + name));
            }
        }

        return found;

        static string? NameOf(MetadataReader reader, EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name),
            HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
            _ => null,
        };
    }

    /// <summary>
    /// The ten assemblies this repository ships, by path — and the assertion that the list IS the
    /// release: every packable project under <c>src/</c> appears here, so a package added later
    /// cannot quietly fall outside a check that says "any shipped assembly".
    /// </summary>
    private static IReadOnlyList<string> ShippedAssemblies()
    {
        string[] names =
        [
            "Affiant.Abstractions",
            "Affiant.Core",
            "Affiant.Docket",
            "Affiant.EntityFramework",
            "Affiant.Policies",
            "Affiant.Transport.SignalR",
            "Affiant.Extensions.AI",
            "Affiant.SemanticKernel",
            "Affiant.AgentFramework",
            "Affiant.Testing.ComplianceHarness",
        ];

        var src = Path.Combine(RepositoryRoot(), "src");
        var packable = Directory
            .EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToArray(), packable);

        var beside = Path.GetDirectoryName(typeof(ConformanceDriverTests).Assembly.Location)!;
        var configuration = new DirectoryInfo(beside).Parent!.Name;

        return
        [
            .. names.Select(name =>
            {
                // Beside this assembly for the five this project references; from the package's own
                // build output for the five it does not — a solution build produces all ten, and
                // referencing five more projects just to look at them would put the whole adapter
                // surface in the driver's dependency graph to answer a question about none of it.
                var path = Path.Combine(beside, name + ".dll");
                if (!File.Exists(path))
                {
                    path = Path.Combine(src, name, "bin", configuration, "net10.0", name + ".dll");
                }

                Assert.True(
                    File.Exists(path),
                    $"{name}.dll was not found beside the test assembly or at {path}. "
                    + "Build the solution before running this suite.");

                return path;
            }),
        ];
    }

    /// <summary>This repository's root, found by walking up for the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(ConformanceDriverTests).Assembly.Location)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Affiant.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("No Affiant.slnx above the test assembly.");
    }

    [Fact]
    public void EveryFixtureTheIndexListsWasRun()
    {
        var run = Run;
        var expected = Suite.Manifest.Select(m => m.Id).ToArray();
        var actual = run.Results.Select(r => r.Id).ToArray();

        // A driver runs every fixture the manifest lists. Running a subset and reporting a pass is
        // the failure mode the whole arrangement exists to prevent.
        Assert.Equal(expected, actual);

        var summary = run.Document["summary"]!;
        output.WriteLine(
            $"conformance {ConformanceRun.ImplementationName}@{ConformanceRun.ImplementationVersion} " +
            $"against protocol {Suite.ProtocolTag}: " +
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
        Assert.DoesNotContain(Run.Results, r => r.Outcome == "skipped");
    }

    [Fact]
    public void TheResultDocumentValidatesAgainstItsSchema()
    {
        var schema = JsonSchema.FromFile(Path.Combine(Suite.Root, "results.schema.json"));
        var result = schema.Evaluate(
            Run.Document,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = false });

        Assert.True(result.IsValid, Explain(result));
    }

    [Fact]
    public void TheFailingSetEqualsTheParityManifest()
    {
        var manifest = ParityManifest.Load();
        Assert.NotNull(manifest);

        var declared = manifest!.FailingIds;
        var observed = Run.FailingIds;

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
        // has no way to express one. The four the rulebook allows: "fixed" (a SHIPPED release
        // corrects it), "planned" (scheduled for a named release), "fenced" (a host-side workaround
        // contains it today) and "ignored" (nothing is being done and nothing is scheduled).
        var undisposed = manifest!.Rows
            .Where(r => r["disposition"]?.GetValue<string>() is not ("fixed" or "planned" or "fenced" or "ignored"))
            .Select(r => r["id"]!.GetValue<string>())
            .ToArray();

        Assert.True(undisposed.Length == 0, $"No disposition on: {Join(undisposed)}");

    }

    [Fact]
    public void TheParityManifestValidatesAgainstItsSchema()
    {
        var manifest = ParityManifest.Load();
        Assert.NotNull(manifest);

        var schema = JsonSchema.FromFile(Path.Combine(Suite.Root, "parity", "MANIFEST.schema.json"));
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
        var rulebook = ProtocolSuite.ReadObject(Path.Combine(Suite.Root, "lint", "coverage-exemptions.json"))
            ["exemptions"]!.AsArray().Select(e => e!["rule"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
        var declared = manifest!.Document["exemptions"]!.AsArray()
            .Select(e => e!["rule"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(rulebook, declared);
    }

    /// <summary>
    /// A fixture whose rule a known defective release violates is accepted into the suite only if it
    /// FAILS against <b>that release</b>. A listed fixture that passes there is not good news: it
    /// means the fixture is mis-authored or the recorded defect is not what it was said to be, and it
    /// is investigated before the tag is cut. It is never tuned into failing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The oracle is a statement about a <em>named</em> release — every entry in the vendored suite
    /// reads <c>dotnet@1.0.0-beta.1</c>. Running it against a different version answers a question
    /// nobody asked: a release that fixes those rules is supposed to pass those fixtures, and
    /// reporting that as a broken oracle would turn every correction into a red build. So on any
    /// other version this reports itself skipped, with the reason, rather than failing or quietly
    /// passing. xUnit 2.x has no dynamic skip, so the skip is a line in the run's own output and an
    /// assertion that the versions really do differ.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryOracleFixtureFailsOnThisRelease()
    {
        var oracles = Suite.Manifest
            .Where(m => m.Oracle is not null)
            .SelectMany(m => m.Oracle!.MustFailOn)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var running = $"{ConformanceRun.ImplementationName}@{ConformanceRun.ImplementationVersion}";
        if (!oracles.Contains(running, StringComparer.Ordinal))
        {
            // Reported, not swallowed. xUnit 2.x has no dynamic skip, so the reason is written where
            // a reader of the run will see it, and the precondition that makes the assertion
            // inapplicable is itself asserted: the day this version is one the oracle names again,
            // this line fails rather than letting the check quietly stop running.
            Assert.DoesNotContain(running, oracles, StringComparer.Ordinal);
            output.WriteLine(
                $"SKIPPED — EveryOracleFixtureFailsOnThisRelease is a statement about " +
                $"{string.Join(", ", oracles)}; this run measured {running}. A release that fixes " +
                "those rules is supposed to pass those fixtures, so the assertion is not answerable " +
                "here. It is not failed and it is not quietly passed: it did not run, and this says " +
                "so.");
            return;
        }

        var outcomes = Run.Results.ToDictionary(r => r.Id, r => r.Outcome, StringComparer.Ordinal);
        var passing = Suite.Manifest
            .Where(m => m.Oracle is not null && m.Oracle.MustFailOn.Contains(running, StringComparer.Ordinal))
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
        var root = Suite.Root;
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
