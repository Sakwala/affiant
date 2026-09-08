using System.Text.Json;
using Affiant.Abstractions.Serialization;

namespace Affiant.Abstractions.Models;

/// <summary>
/// Extensions for serializing <see cref="ToolEnvelope"/> variants to JSON strings.
/// Plugins call <c>envelope.ToJsonString()</c> from their <c>[KernelFunction]</c>
/// methods to bridge the gap between typed envelopes and SK's <c>Task&lt;string&gt;</c>
/// return convention.
/// </summary>
public static class ToolEnvelopeExtensions
{
    /// <summary>
    /// Serializes a <see cref="ToolEnvelope"/> variant to JSON with the <c>kind</c> discriminator
    /// (AF-5) under the framework's one set of JSON conventions
    /// (<see cref="AffiantJson.SerializerOptions"/>, SR-3).
    ///
    /// <para>
    /// It used to configure its own options — camelCase, and nothing else. No published version
    /// crossed an enum inconsistently for it: <see cref="ProvenanceSource"/>, the only enum inside
    /// an envelope, has always carried a type-level <c>[JsonConverter(typeof(JsonStringEnumConverter))]</c>
    /// and so serialized as a string either way. This type now shares <see cref="AffiantJson"/>'s
    /// options so a future enum in the envelope gets the same guarantee without repeating it here.
    /// </para>
    /// </summary>
    public static string ToJsonString(this ToolEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, AffiantJson.SerializerOptions);
}
