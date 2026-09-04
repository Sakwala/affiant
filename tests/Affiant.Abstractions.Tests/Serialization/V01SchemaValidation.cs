namespace Affiant.Abstractions.Tests.Serialization;

using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Abstractions.Serialization;
using Json.Schema;

/// <summary>
/// Loads the vendored v0.1 schemas once and evaluates an envelope against one of them.
///
/// <para>
/// The schemas are the rulebook's own files under <c>tests/protocol/schemas/0.1.0</c>, copied from
/// <c>Sakwala/affiant-protocol</c> at the tag <c>v0.1.1</c> (commit <c>8530987</c>). They are
/// unchanged from <c>v0.1.0</c>: that release patched the conformance vectors and the fixture lint,
/// not the wire. They <c>$ref</c> each other by absolute
/// <c>$id</c>, so every one of them is registered before any evaluation runs — a schema library that
/// fetched an unregistered <c>$id</c> over the network would make this suite depend on the internet,
/// and it does not.
/// </para>
/// </summary>
internal static class V01SchemaValidation
{
    private static readonly Lazy<Dictionary<Uri, JsonSchema>> Schemas = new(Load);

    /// <summary>
    /// Serialize <paramref name="envelope"/> the way the framework puts it on the wire, then report
    /// every place the v0.1 schema named by <paramref name="schema"/> disagrees with it.
    /// </summary>
    /// <returns>
    /// One line per disagreement — <c>&lt;JSON pointer into the instance&gt; :: &lt;keyword&gt;</c> —
    /// sorted, de-duplicated, and empty when the envelope conforms.
    /// </returns>
    public static IReadOnlyList<string> Violations(object envelope, string schema) =>
        Violations(Wire(envelope), schema);

    /// <summary>Report every place the named v0.1 schema disagrees with an already-parsed document.</summary>
    public static IReadOnlyList<string> Violations(JsonNode? instance, string schema)
    {
        var results = Resolve(schema).Evaluate(instance, Options());
        if (results.IsValid) return [];

        var lines = new SortedSet<string>(StringComparer.Ordinal);
        Collect(results, lines);
        return [.. lines];
    }

    /// <summary><paramref name="envelope"/> as the JSON the framework would put on the wire.</summary>
    public static JsonNode Wire(object envelope) =>
        JsonNode.Parse(JsonSerializer.Serialize(envelope, AffiantJson.SerializerOptions))!;

    private static void Collect(EvaluationResults results, SortedSet<string> lines)
    {
        if (results.IsValid) return;

        if (results.HasErrors)
        {
            var where = results.InstanceLocation.ToString();
            foreach (var (keyword, _) in results.Errors!)
            {
                // A property the schema does not admit is reported at the property's own location
                // with no keyword name — the `false` subschema `additionalProperties: false`
                // produces. Give it a name so a reader of a failing list can tell it from a typed
                // value that came out wrong.
                lines.Add($"{(where.Length == 0 ? "/" : where)} :: {(keyword.Length == 0 ? "not-admitted" : keyword)}");
            }
        }

        foreach (var child in results.Details)
            Collect(child, lines);
    }

    private static EvaluationOptions Options()
    {
        // Hierarchical, not List: a flat list carries every node including the failed branches of a
        // oneOf that SUCCEEDED, and reporting those as violations would name three problems where
        // there is none. The tree lets a passing node prune its own subtree.
        var options = new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical };
        foreach (var (id, schema) in Schemas.Value) options.SchemaRegistry.Register(id, schema);
        return options;
    }

    private static JsonSchema Resolve(string name) =>
        Schemas.Value.TryGetValue(SchemaId(name), out var schema)
            ? schema
            : throw new InvalidOperationException($"No vendored v0.1 schema is named \"{name}\".");

    private static Uri SchemaId(string name) => new($"https://affiant.dev/schemas/0.1.0/{name}.schema.json");

    private static Dictionary<Uri, JsonSchema> Load()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "protocol", "schemas", "0.1.0");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The vendored v0.1 schemas are missing from {directory}.");

        var loaded = new Dictionary<Uri, JsonSchema>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.schema.json"))
        {
            var schema = JsonSchema.FromFile(file);
            loaded[schema.GetId() ?? new Uri(file)] = schema;
        }

        return loaded;
    }
}
