using System.Globalization;
using System.Text.Json.Nodes;
using Affiant.Abstractions.Models;
using Affiant.Testing.ComplianceHarness.Conformance.Model;

namespace Affiant.Testing.ComplianceHarness.Conformance.Ports;

/// <summary>
/// A fixture's binding (<c>{ kind, ref }</c>) as the framework's own
/// <see cref="ProvenanceBinding"/>, and back again.
/// </summary>
/// <remarks>
/// The mapping is by kind, one arm each, and it refuses a kind the rulebook does not define rather
/// than dropping it: a binding the driver silently discarded would turn "the implementation lost
/// the binding" into "the driver never offered one", and those are different findings (PV-2, PV-3).
/// </remarks>
internal static class Bindings
{
    /// <summary>The binding a fixture states, as the framework holds it.</summary>
    public static ProvenanceBinding? ToFramework(BindingSpec? spec)
    {
        if (spec is null)
            return null;

        var r = spec.Ref;
        return spec.Kind switch
        {
            ProvenanceBindingKind.UtteranceSpan => new ProvenanceBinding.UtteranceSpan(
                new UtteranceSpanRef(Int(r, "offset"), Int(r, "length"), Str(r, "hash") ?? string.Empty)),
            ProvenanceBindingKind.ReviewerAct => new ProvenanceBinding.ReviewerAct(
                new ReviewerActRef(Guid.TryParse(Str(r, "entryId"), out var id) ? id : Guid.Empty, Instant(r, "decisionAt"))),
            ProvenanceBindingKind.FormInput => new ProvenanceBinding.FormInput(
                new FormInputRef(Str(r, "field") ?? string.Empty)),
            ProvenanceBindingKind.ExternalRef => new ProvenanceBinding.ExternalRef(
                new ExternalRecordRef(
                    Str(r, "system") ?? string.Empty,
                    Str(r, "recordId") ?? string.Empty,
                    OptionalInstant(r, "fetchedAt"),
                    Str(r, "contentHash"),
                    Relay(r["relay"] as JsonObject))),
            ProvenanceBindingKind.ComputationRef => new ProvenanceBinding.ComputationRef(
                new ComputationRuleRef(
                    Str(r, "rule") ?? string.Empty,
                    (r["inputs"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? string.Empty).ToArray() ?? [],
                    Constant(r["constant"] as JsonObject))),
            _ => throw new InvalidOperationException(
                $"'{spec.Kind}' is not one of the rulebook's five binding kinds."),
        };
    }

    private static RelayRef? Relay(JsonObject? o) => o is null
        ? null
        : new RelayRef(Str(o, "principal") ?? string.Empty, Str(o, "channelIdentity") ?? string.Empty, Str(o, "messageId") ?? string.Empty);

    private static ComputationConstantRef? Constant(JsonObject? o) => o is null
        ? null
        : new ComputationConstantRef(Str(o, "source") ?? string.Empty, Str(o, "verifiedOn") ?? string.Empty);

    private static string? Str(JsonObject o, string key) => o[key]?.GetValue<string>();

    private static int Int(JsonObject o, string key) => o[key] is { } n ? (int)n.GetValue<double>() : 0;

    private static DateTimeOffset Instant(JsonObject o, string key) =>
        OptionalInstant(o, key) ?? DateTimeOffset.UnixEpoch;

    private static DateTimeOffset? OptionalInstant(JsonObject o, string key) =>
        Str(o, key) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
}
