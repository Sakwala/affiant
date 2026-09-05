using System.Text.Json;
using System.Text.Json.Serialization;
using Affiant.Abstractions.Models;

namespace Affiant.Abstractions.Serialization;

/// <summary>
/// Reads and writes a <see cref="ProvenanceBinding"/> as <c>{ "kind": …, "ref": { … } }</c>,
/// wherever the discriminator happens to sit in the object.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not left to <c>[JsonPolymorphic]</c>.</b> System.Text.Json's polymorphic reader
/// requires the discriminator to be the FIRST property of the object, and a binding does not always
/// arrive that way. PostgreSQL's <c>jsonb</c> — the column type the Docket's Affidavit is stored in,
/// chosen for its indexes — does not preserve key order: it stores keys sorted by length and then
/// by bytes, so a binding written <c>{"kind":"reviewer-act","ref":{…}}</c> comes back
/// <c>{"ref":{…},"kind":"reviewer-act"}</c> and the polymorphic reader refuses it as having no
/// discriminator at all. Every row carrying a reviewer's amendment would then be unreadable from
/// the moment it was written — the strongest claim a tag can make (PV-2), lost to a key-order
/// detail of the store.
/// </para>
/// <para>
/// The written form is unchanged: the discriminator first, then the payload under <c>ref</c>, both
/// spelled exactly as the wire schema defines them, so the same bytes still read the same way
/// wherever they came from.
/// </para>
/// </remarks>
public sealed class ProvenanceBindingConverter : JsonConverter<ProvenanceBinding>
{
    /// <inheritdoc />
    public override ProvenanceBinding? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (reader.TokenType == JsonTokenType.Null) return null;

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                "PV-2: a provenance binding says which of the five kinds it is, under \"kind\". A " +
                "binding with no kind names no artifact an auditor could check, and the reader will " +
                "not guess one.");
        }

        var payload = root.TryGetProperty("ref", out var reference) ? reference : default;
        var name = kind.GetString();

        return name switch
        {
            ProvenanceBindingKind.UtteranceSpan =>
                new ProvenanceBinding.UtteranceSpan(Payload<UtteranceSpanRef>(payload, name, options)),
            ProvenanceBindingKind.ReviewerAct =>
                new ProvenanceBinding.ReviewerAct(Payload<ReviewerActRef>(payload, name, options)),
            ProvenanceBindingKind.FormInput =>
                new ProvenanceBinding.FormInput(Payload<FormInputRef>(payload, name, options)),
            ProvenanceBindingKind.ExternalRef =>
                new ProvenanceBinding.ExternalRef(Payload<ExternalRecordRef>(payload, name, options)),
            ProvenanceBindingKind.ComputationRef =>
                new ProvenanceBinding.ComputationRef(Payload<ComputationRuleRef>(payload, name, options)),
            _ => throw new JsonException(
                $"PV-2: \"{name}\" is not one of the five binding kinds. The set is fixed — a binding " +
                "kind nobody can enumerate is a binding nobody can audit."),
        };
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, ProvenanceBinding value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        writer.WritePropertyName("ref");

        switch (value)
        {
            case ProvenanceBinding.UtteranceSpan span:
                JsonSerializer.Serialize(writer, span.Ref, options);
                break;
            case ProvenanceBinding.ReviewerAct act:
                JsonSerializer.Serialize(writer, act.Ref, options);
                break;
            case ProvenanceBinding.FormInput form:
                JsonSerializer.Serialize(writer, form.Ref, options);
                break;
            case ProvenanceBinding.ExternalRef external:
                JsonSerializer.Serialize(writer, external.Ref, options);
                break;
            case ProvenanceBinding.ComputationRef computation:
                JsonSerializer.Serialize(writer, computation.Ref, options);
                break;
            default:
                throw new JsonException(
                    $"PV-2: {value.GetType().Name} is not one of the five binding kinds.");
        }

        writer.WriteEndObject();
    }

    private static T Payload<T>(JsonElement payload, string? kind, JsonSerializerOptions options)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new JsonException(
                $"PV-2: a \"{kind}\" binding carries its payload under \"ref\", and this one carries " +
                "none. A binding whose source cannot be re-fetched or re-verified is not a binding.");
        }

        return payload.Deserialize<T>(options)
            ?? throw new JsonException($"PV-2: the \"ref\" of a \"{kind}\" binding could not be read.");
    }
}
