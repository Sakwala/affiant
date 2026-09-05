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
    /// It used to configure its own options — camelCase, and nothing else — so an enum inside a tool
    /// result crossed as an integer while the same enum inside an Evidence Card crossed as a string.
    /// The two now agree.
    /// </para>
    /// </summary>
    public static string ToJsonString(this ToolEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, AffiantJson.SerializerOptions);
}
