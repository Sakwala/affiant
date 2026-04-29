using System.Text.Json;

namespace Affiant.Abstractions.Models;

/// <summary>
/// Extensions for serializing <see cref="ToolEnvelope"/> variants to JSON strings.
/// Plugins call <c>envelope.ToJsonString()</c> from their <c>[KernelFunction]</c>
/// methods to bridge the gap between typed envelopes and SK's <c>Task&lt;string&gt;</c>
/// return convention.
/// </summary>
public static class ToolEnvelopeExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Serializes a <see cref="ToolEnvelope"/> variant to JSON with the polymorphic
    /// <c>$type</c> discriminator and camelCase property names.
    /// </summary>
    public static string ToJsonString(this ToolEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, SerializerOptions);
}
