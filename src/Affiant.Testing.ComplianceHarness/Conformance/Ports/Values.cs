using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Affiant.Testing.ComplianceHarness.Conformance.Ports;

/// <summary>
/// The one place a fixture's JSON value becomes a CLR value and a framework value becomes JSON
/// again. Both directions live together so a round trip cannot drift, and so the whole suite has
/// exactly one answer to "what is a number".
/// </summary>
internal static class Values
{
    /// <summary>A fixture's value as the framework would hold it.</summary>
    public static object? ToClr(JsonNode? node) => node?.GetValueKind() switch
    {
        null or JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => node!.GetValue<string>(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => Number(node!),
        _ => node!.ToJsonString(),
    };

    /// <summary>A coverage category, as a fixture spells it (CV-4).</summary>
    public static Affiant.Abstractions.Models.CoverageCategory Category(string category) => category switch
    {
        "no-execute" => Affiant.Abstractions.Models.CoverageCategory.NoExecute,
        "provider-executed" => Affiant.Abstractions.Models.CoverageCategory.ProviderExecuted,
        "hosted-mcp" => Affiant.Abstractions.Models.CoverageCategory.HostedMcp,
        _ => throw new ArgumentOutOfRangeException(
            nameof(category), category, "CV-4 fixes the set of coverage categories at three."),
    };

    /// <summary>A framework value as the fixture would state it.</summary>
    public static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        float f => JsonValue.Create((double)f),
        double d => JsonValue.Create(d),
        decimal m => JsonValue.Create((double)m),
        DateTimeOffset o => JsonValue.Create(o.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
        JsonNode n => n.DeepClone(),
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        System.Collections.IDictionary map => Map(map),
        System.Collections.IEnumerable list and not string => List(list),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static object Number(JsonNode node)
    {
        var d = node.GetValue<double>();
        return d == Math.Floor(d) && Math.Abs(d) < int.MaxValue ? (int)d : d;
    }

    private static JsonObject Map(System.Collections.IDictionary map)
    {
        var o = new JsonObject();
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            o[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!] = ToJson(entry.Value);
        }

        return o;
    }

    private static JsonArray List(System.Collections.IEnumerable list)
    {
        var a = new JsonArray();
        foreach (var item in list)
        {
            a.Add(ToJson(item));
        }

        return a;
    }
}
