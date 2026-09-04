namespace Affiant.Abstractions.Tests.Telemetry;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// The subset of JSON Schema draft 2020-12 the rulebook's <c>telemetry-key.schema.json</c> actually
/// uses, applied to a document: <c>type</c>, <c>required</c>, <c>properties</c>,
/// <c>additionalProperties: false</c>, <c>items</c>, <c>minItems</c>, <c>minLength</c>,
/// <c>pattern</c>, and <c>$ref</c> to a <c>$defs</c> entry in this document or in a companion
/// document supplied by the caller.
///
/// <para>
/// <b>Why hand-written.</b> Validating this repository's registry against the rulebook's schema
/// needs a validator; pulling a JSON Schema package in for one file's worth of keywords would add a
/// dependency to a test project for a job smaller than the dependency. The honest cost of the
/// choice is that this understands only the keywords listed above — it is not a general-purpose
/// validator, and it says so by throwing <see cref="NotSupportedException"/> on a keyword it does
/// not implement rather than quietly ignoring it. That is the property that keeps it safe as the
/// rulebook's schema evolves: a new constraint fails the suite loudly instead of passing silently.
/// </para>
///
/// <para>
/// <c>TelemetryKeyRegistryTests</c> runs it against the rulebook's own positive and negative
/// fixtures, so a change that made it vacuous would fail.
/// </para>
/// </summary>
internal static class JsonSchemaChecker
{
    private static readonly HashSet<string> KnownKeywords =
    [
        "$schema", "$id", "$defs", "$ref", "$comment", "title", "description",
        "type", "required", "properties", "additionalProperties", "items",
        "minItems", "minLength", "pattern",
    ];

    /// <summary>
    /// Validates <paramref name="instance"/> against <paramref name="schema"/>, returning every
    /// violation found. An empty list means the document is valid.
    /// </summary>
    /// <param name="instance">The document being checked.</param>
    /// <param name="schema">The schema document's root.</param>
    /// <param name="externalDocuments">
    /// Schema documents an external <c>$ref</c> may resolve into, keyed by the file name the
    /// <c>$ref</c> uses (for example <c>common.schema.json</c>).
    /// </param>
    public static IReadOnlyList<string> Validate(
        JsonElement instance,
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement>? externalDocuments = null)
    {
        var violations = new List<string>();
        Check(instance, schema, schema, externalDocuments ?? new Dictionary<string, JsonElement>(), "$", violations);
        return violations;
    }

    private static void Check(
        JsonElement instance,
        JsonElement schema,
        JsonElement root,
        IReadOnlyDictionary<string, JsonElement> externals,
        string path,
        List<string> violations)
    {
        foreach (var keyword in schema.EnumerateObject())
        {
            if (!KnownKeywords.Contains(keyword.Name))
            {
                throw new NotSupportedException(
                    $"JsonSchemaChecker does not implement the JSON Schema keyword '{keyword.Name}' " +
                    $"(at {path}). The rulebook's schema has gained a constraint this test cannot " +
                    "check — implement it here rather than deleting the assertion.");
            }
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            Check(instance, Resolve(reference.GetString()!, root, externals), root, externals, path, violations);
            return;
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var expected = type.GetString();
            if (!Matches(instance, expected!))
            {
                violations.Add($"{path}: expected type '{expected}', found {instance.ValueKind}.");
                return;
            }
        }

        if (instance.ValueKind == JsonValueKind.Object) CheckObject(instance, schema, root, externals, path, violations);
        if (instance.ValueKind == JsonValueKind.Array) CheckArray(instance, schema, root, externals, path, violations);
        if (instance.ValueKind == JsonValueKind.String) CheckString(instance, schema, path, violations);
    }

    private static void CheckObject(
        JsonElement instance,
        JsonElement schema,
        JsonElement root,
        IReadOnlyDictionary<string, JsonElement> externals,
        string path,
        List<string> violations)
    {
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray())
            {
                if (!instance.TryGetProperty(name.GetString()!, out _))
                    violations.Add($"{path}: required property '{name.GetString()}' is missing.");
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties);

        if (schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.False)
        {
            foreach (var member in instance.EnumerateObject())
            {
                if (!hasProperties || !properties.TryGetProperty(member.Name, out _))
                    violations.Add($"{path}: property '{member.Name}' is not allowed.");
            }
        }

        if (!hasProperties) return;

        foreach (var property in properties.EnumerateObject())
        {
            if (instance.TryGetProperty(property.Name, out var value))
                Check(value, property.Value, root, externals, $"{path}.{property.Name}", violations);
        }
    }

    private static void CheckArray(
        JsonElement instance,
        JsonElement schema,
        JsonElement root,
        IReadOnlyDictionary<string, JsonElement> externals,
        string path,
        List<string> violations)
    {
        if (schema.TryGetProperty("minItems", out var minItems)
            && instance.GetArrayLength() < minItems.GetInt32())
        {
            violations.Add($"{path}: expected at least {minItems.GetInt32()} item(s), found {instance.GetArrayLength()}.");
        }

        if (!schema.TryGetProperty("items", out var items)) return;

        var index = 0;
        foreach (var item in instance.EnumerateArray())
            Check(item, items, root, externals, $"{path}[{index++}]", violations);
    }

    private static void CheckString(JsonElement instance, JsonElement schema, string path, List<string> violations)
    {
        var value = instance.GetString()!;

        if (schema.TryGetProperty("minLength", out var minLength) && value.Length < minLength.GetInt32())
            violations.Add($"{path}: expected at least {minLength.GetInt32()} character(s), found {value.Length}.");

        if (schema.TryGetProperty("pattern", out var pattern)
            && !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.None, TimeSpan.FromSeconds(2)))
        {
            violations.Add($"{path}: '{value}' does not match pattern '{pattern.GetString()}'.");
        }
    }

    private static JsonElement Resolve(
        string reference, JsonElement root, IReadOnlyDictionary<string, JsonElement> externals)
    {
        var separator = reference.IndexOf('#', StringComparison.Ordinal);
        if (separator < 0)
            throw new NotSupportedException($"JsonSchemaChecker cannot resolve the whole-document $ref '{reference}'.");

        var document = reference[..separator];
        var pointer = reference[(separator + 1)..];

        var target = root;
        if (document.Length > 0)
        {
            var fileName = document[(document.LastIndexOf('/') + 1)..];
            if (!externals.TryGetValue(fileName, out target))
            {
                throw new NotSupportedException(
                    $"JsonSchemaChecker cannot resolve the $ref '{reference}': no schema document " +
                    $"named '{fileName}' was supplied. Vendor it next to the others and pass it in.");
            }
        }

        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!target.TryGetProperty(segment.Replace("~1", "/").Replace("~0", "~"), out target))
                throw new NotSupportedException($"JsonSchemaChecker cannot resolve the $ref '{reference}'.");
        }

        return target;
    }

    private static bool Matches(JsonElement instance, string type) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
        "number" => instance.ValueKind == JsonValueKind.Number,
        _ => throw new NotSupportedException($"JsonSchemaChecker does not implement the JSON Schema type '{type}'."),
    };
}
