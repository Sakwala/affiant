using System.Text.Json;
using System.Text.Json.Serialization;
using Affiant.Abstractions.Models;

namespace Affiant.Abstractions.Serialization;

/// <summary>
/// The JSON conventions every Affiant envelope is written under, declared once.
///
/// <para>
/// <b>SR-3</b> — <i>camelCase property names; enums as strings; explicit <c>null</c> for a null
/// value; enum values written exactly as the schema spells them, and no implementation case-folds
/// one on the wire.</i>
/// </para>
///
/// <para>
/// <b>Why one object and not a set of conventions each caller repeats.</b> Before this existed the
/// framework had three: the SignalR hub protocol configured camelCase and string enums;
/// <c>ToolEnvelopeExtensions</c> configured camelCase and nothing else, so an enum inside a tool
/// result crossed as an integer while the same enum inside an Evidence Card crossed as a string;
/// and anything a host serialized itself inherited whatever its own defaults were. Three spellings
/// of one record is exactly the drift SR-3 names, and a canonical form (SR-1) computed under one of
/// them does not match a hash computed under another.
/// </para>
///
/// <para>
/// <b>What is configured, and which rule asks for it:</b>
/// <list type="bullet">
/// <item><b>camelCase names</b> (SR-3), which is what the hub protocol already produced.</item>
/// <item><b>Enums as strings</b> (SR-3), in the exact casing each schema freezes: provenance
/// sources PascalCase — <c>"UserStated"</c> — because that is how
/// <c>schemas/0.1.0/provenance-source.schema.json</c> spells them; Docket statuses lowercase —
/// <c>"pending"</c> — because that is how <c>docket-entry.schema.json</c> spells them. A single
/// blanket converter cannot do both, so the per-enum converters below are registered by name.</item>
/// <item><b>Nulls written</b> (SR-1, SR-3): a required-and-nullable property is written
/// <c>null</c> rather than omitted, so a reader never has to tell "unbound" from "the property was
/// left off". A property the schemas mark <i>optional</i> is omitted when it has nothing to say,
/// which is the per-property <c>[JsonIgnore(WhenWritingNull)]</c> on those few.</item>
/// <item><b>Instants in one spelling</b> — see <see cref="IsoInstantJsonConverter"/>.</item>
/// <item><b>Money as its two strings</b> — see <see cref="Money"/>; the converter also refuses a
/// JSON number where money was expected (SR-2).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>This is not a transport.</b> SR-5 keeps the protocol independent of SignalR, SSE, REST or MCP;
/// what this type fixes is how a value is spelled once some transport carries it. A host serializing
/// an Affiant record itself should use <see cref="SerializerOptions"/> — or call
/// <see cref="Configure"/> on its own options object — so its bytes and the framework's agree.
/// </para>
/// </summary>
public static class AffiantJson
{
    /// <summary>
    /// The options every Affiant envelope is serialized with. Frozen: read it, do not mutate it —
    /// call <see cref="Configure"/> on an options object of your own instead.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateFrozen();

    /// <summary>
    /// Apply Affiant's JSON conventions to <paramref name="options"/>, leaving everything else on it
    /// alone.
    ///
    /// This is the seam a transport uses: the SignalR hub protocol calls it on the payload
    /// serializer options ASP.NET Core hands it, so a hub payload and a canonical form are written
    /// the same way without the transport package restating the conventions.
    /// </summary>
    /// <param name="options">The options to configure. Must not already be read-only.</param>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;

        // Provenance sources are PascalCase on the wire and Docket statuses are lowercase, because
        // that is how the v0.1 schemas spell each set; SR-3 freezes both as they stand and forbids
        // case-folding either. Registering the general string-enum converter LAST means the two
        // named converters win for their own types and every other enum still crosses as a string.
        options.Converters.Add(new IsoInstantJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter<ReviewStatus>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter());
    }

    private static JsonSerializerOptions CreateFrozen()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        Configure(options);

        // populateMissingResolver: the reflection-based resolver is what every other path in this
        // framework already uses; freezing without one throws, and freezing is what makes this
        // object safe to hand out as a static.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
