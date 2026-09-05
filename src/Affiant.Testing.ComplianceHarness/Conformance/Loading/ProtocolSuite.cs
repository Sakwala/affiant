using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Testing.ComplianceHarness.Conformance.Model;
using Json.Schema;

namespace Affiant.Testing.ComplianceHarness.Conformance.Loading;

/// <summary>
/// One vendored rulebook on disk: the fixture index, the documents themselves, the two schemas they
/// are checked against, and the telemetry registry a fixture's telemetry clause may name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything comes from the root the caller named, and nothing from anywhere else.</b> A run is
/// a measurement against a document set a reader can check; a suite that took its fixtures from one
/// place and the schema they are validated against from another would be holding a caller's
/// rulebook to a different rulebook's rules, and neither the caller nor the reader of the report
/// would be told. There is no ambient copy, no beside-the-assembly fallback and no static instance:
/// a root is stated, or nothing runs.
/// </para>
/// <para>
/// The framework's own copy is vendored from the commit <c>conformance/PROTOCOL_PIN</c> names and
/// verified against <c>SHA256SUMS</c> by <c>conformance/sync.sh --verify</c>, which CI runs, so its
/// driver builds and runs offline and an edited fixture cannot pass unnoticed. A consumer vendors
/// the same way, in its own repository.
/// </para>
/// </remarks>
internal sealed class ProtocolSuite
{
    /// <summary>What a rulebook root must carry, and what each file is for.</summary>
    private static readonly (string Path, string Purpose)[] Required =
    [
        (Path.Combine("fixtures", "MANIFEST.json"), "the index of every conformance document to run"),
        ("fixture.schema.json", "the schema every declarative fixture is validated against"),
        ("canonical-vector.schema.json", "the schema every canonical byte vector is validated against"),
        (Path.Combine("fixtures", "v0.1", "telemetry-key", "01-registry.json"), "the telemetry-key registry (TL-1)"),
    ];

    private ProtocolSuite(string root)
    {
        Root = root;
    }

    /// <summary>The vendored rulebook's root directory.</summary>
    public string Root { get; }

    /// <summary>The protocol ref this copy came from, as <c>conformance/PROTOCOL_PIN</c> records it.</summary>
    public string ProtocolTag => _tag ??= ReadPin(Root);

    private string? _tag;

    /// <summary>The schema every declarative fixture is held against, from this root.</summary>
    public JsonSchema FixtureSchema => _fixtureSchema ??= JsonSchema.FromFile(Path.Combine(Root, "fixture.schema.json"));

    private JsonSchema? _fixtureSchema;

    /// <summary>The schema every canonical byte vector is held against, from this root.</summary>
    public JsonSchema CanonicalVectorSchema =>
        _vectorSchema ??= JsonSchema.FromFile(Path.Combine(Root, "canonical-vector.schema.json"));

    private JsonSchema? _vectorSchema;

    /// <summary>Every conformance document the index lists — the whole set a driver must run.</summary>
    public IReadOnlyList<ManifestEntry> Manifest => _manifest ??= LoadManifest();

    private IReadOnlyList<ManifestEntry>? _manifest;

    /// <summary>The telemetry keys the registry knows (TL-1). A clause naming anything else fails its fixture.</summary>
    public IReadOnlySet<string> TelemetryRegistry => _telemetry ??= LoadTelemetryRegistry();

    private IReadOnlySet<string>? _telemetry;

    /// <summary>The path a manifest row's <c>file</c> resolves to.</summary>
    public string FixturePath(ManifestEntry entry) => Path.Combine(Root, "fixtures", entry.File.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The document at a path, parsed. Duplicate keys and trailing commas are refused: this is a document, not a config file.</summary>
    public static JsonObject ReadObject(string path)
    {
        using var stream = File.OpenRead(path);
        var node = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false })
            ?? throw new InvalidOperationException($"{path} parsed to JSON null.");
        return node.AsObject();
    }

    private IReadOnlyList<ManifestEntry> LoadManifest()
    {
        var manifest = ReadObject(Path.Combine(Root, "fixtures", "MANIFEST.json"));
        var section = manifest["conformance"]?.AsObject()
            ?? throw new InvalidOperationException("fixtures/MANIFEST.json has no \"conformance\" section.");
        var rows = section["fixtures"]?.AsArray()
            ?? throw new InvalidOperationException("fixtures/MANIFEST.json \"conformance\" has no \"fixtures\".");

        var entries = new List<ManifestEntry>(rows.Count);
        foreach (var row in rows)
        {
            var o = row!.AsObject();
            var oracle = o["oracle"] is JsonObject or
                ? new OracleEntry(
                    or["mustFailOn"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray(),
                    or["defect"]!.GetValue<string>())
                : null;
            entries.Add(new ManifestEntry(
                o["id"]!.GetValue<string>(),
                o["file"]!.GetValue<string>(),
                o["rules"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray(),
                o["set"]!.GetValue<string>(),
                oracle));
        }

        return entries;
    }

    private IReadOnlySet<string> LoadTelemetryRegistry()
    {
        var registry = ReadObject(Path.Combine(Root, "fixtures", "v0.1", "telemetry-key", "01-registry.json"));
        return registry["keys"]!.AsArray().Select(k => k!["key"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The vendored rulebook at <paramref name="root"/>, checked before anything runs.
    /// </summary>
    /// <remarks>
    /// Every file a run reads comes from here. A root that is missing one of them fails with one
    /// exception naming the root, the file and what that file is for — before a single fixture is
    /// executed, because a run that started and then failed halfway through would leave a caller
    /// reading a partial report as though it were a measurement.
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
    /// <exception cref="FileNotFoundException">The root is missing a file a run reads.</exception>
    public static ProtocolSuite At(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException(
                $"No vendored rulebook at {full}: the directory does not exist. The root is the "
                + "directory holding fixtures/, fixture.schema.json and canonical-vector.schema.json "
                + "as Sakwala/affiant-protocol publishes them, vendored into your own repository.");
        }

        var missing = Required
            .Where(r => !File.Exists(Path.Combine(full, r.Path)))
            .Select(r => $"  {r.Path} — {r.Purpose}")
            .ToArray();

        if (missing.Length > 0)
        {
            throw new FileNotFoundException(
                $"The vendored rulebook at {full} is missing {missing.Length} file(s) a conformance "
                + $"run reads:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}"
                + $"{Environment.NewLine}The root is the directory holding fixtures/, "
                + "fixture.schema.json and canonical-vector.schema.json as Sakwala/affiant-protocol "
                + "publishes them.");
        }

        return new ProtocolSuite(full);
    }

    private static string ReadPin(string root)
    {
        // PROTOCOL_PIN travels with the repository that vendored this copy, not with the copy
        // itself, so it is found by walking up FROM THE ROOT — the caller's own tree, wherever that
        // is. A caller that vendored without a pin file reports "unpinned" rather than a tag nobody
        // pinned; the run document says which it was.
        var dir = new DirectoryInfo(root);
        while (dir is not null)
        {
            var pin = Path.Combine(dir.FullName, "conformance", "PROTOCOL_PIN");
            if (File.Exists(pin))
            {
                var tag = Value(File.ReadAllLines(pin), "tag");
                return string.IsNullOrWhiteSpace(tag) ? Value(File.ReadAllLines(pin), "commit") : tag;
            }

            dir = dir.Parent;
        }

        return "unpinned";

        static string Value(string[] lines, string key)
        {
            var prefix = key + "=";
            var line = lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
            return line is null ? string.Empty : line[prefix.Length..].Trim();
        }
    }
}
