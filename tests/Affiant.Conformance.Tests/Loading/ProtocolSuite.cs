using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Conformance.Tests.Model;

namespace Affiant.Conformance.Tests.Loading;

/// <summary>
/// The vendored rulebook on disk: the fixture index, the documents themselves, the schema they are
/// checked against and the telemetry registry a fixture's telemetry clause may name.
/// </summary>
/// <remarks>
/// The copy under <c>protocol/</c> is vendored from the commit <c>conformance/PROTOCOL_PIN</c>
/// names and verified against <c>SHA256SUMS</c> by <c>conformance/sync.sh --verify</c>, which CI
/// runs. The driver therefore builds and runs offline, and an edited fixture cannot pass unnoticed.
/// </remarks>
internal sealed class ProtocolSuite
{
    private ProtocolSuite(string root)
    {
        Root = root;
    }

    /// <summary>The vendored rulebook's root directory.</summary>
    public string Root { get; }

    /// <summary>The one instance for a run; the suite is read-only and loading it twice is waste.</summary>
    public static ProtocolSuite Instance { get; } = Locate();

    /// <summary>The protocol ref the vendored copy came from, as <c>conformance/PROTOCOL_PIN</c> records it.</summary>
    public static string ProtocolTag { get; } = ReadPin();

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

    private static ProtocolSuite Locate()
    {
        var beside = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "protocol");
        if (Directory.Exists(Path.Combine(beside, "fixtures")))
        {
            return new ProtocolSuite(beside);
        }

        throw new DirectoryNotFoundException(
            $"The vendored rulebook is not beside the test assembly ({beside}). Run conformance/sync.sh.");
    }

    private static string ReadPin()
    {
        // PROTOCOL_PIN travels with the repository, not the build output, so it is found by walking
        // up from the assembly; when it is not there (a packaged run), the vendored README carries
        // the same ref and the pin falls back to it rather than reporting a tag nobody pinned.
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
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
