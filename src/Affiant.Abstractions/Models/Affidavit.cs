namespace Affiant.Abstractions.Models;

/// <summary>
/// The set of values <see cref="AffidavitField.Kind"/> may hold, defined as string
/// constants (not an enum) in this one place so every producer and consumer references
/// the same literals. Kept as a plain string on the wire deliberately — an enum type
/// here would need a JSON converter, and converter behavior has drifted between the
/// SignalR and plain-JSON transports before.
/// </summary>
public static class AffidavitFieldKind
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";
    public const string Enum = "enum";
}

/// <summary>
/// A single sworn field inside an <see cref="Affidavit"/>. Carries the proposed
/// value, the previous value (null for create operations), and the full
/// <see cref="ProvenanceChain"/> — the audit trail for this field's value.
///
/// Matches framework specification §2.6.
/// </summary>
/// <param name="Kind">
/// The reviewer-UI rendering hint for this field's value: one of the
/// <see cref="AffidavitFieldKind"/> constants ("text", "number", "date", "enum").
/// Deliberately a plain string rather than an enum — see <see cref="AffidavitFieldKind"/>.
/// Defaults to <see cref="AffidavitFieldKind.Text"/> so every existing construction site
/// compiles unchanged.
/// </param>
/// <param name="AllowedValues">
/// The closed set of values a reviewer may pick when <see cref="Kind"/> is
/// <see cref="AffidavitFieldKind.Enum"/>; null otherwise.
/// </param>
/// <param name="Pattern">
/// An optional validation regex the field's value must satisfy, forwarded from the
/// originating <c>TaskInferenceField.Pattern</c> when present.
/// </param>
public sealed record AffidavitField(
    string Name,
    object? Value,
    object? PreviousValue,
    ProvenanceChain Provenance,
    bool IsMandatory = false,
    string Kind = AffidavitFieldKind.Text,
    IReadOnlyList<string>? AllowedValues = null,
    string? Pattern = null);

/// <summary>
/// The sworn evidence report for a proposed mutation. Every proposed write
/// (create, update, delete) flows through an Affidavit, carrying full provenance
/// for every field.
///
/// The <see cref="EntityType"/> + <see cref="EntityId"/> pair identifies which domain
/// entity is being mutated; <see cref="EntityId"/> is null for create operations.
///
/// Matches framework specification §2.6.
/// </summary>
public sealed record Affidavit(
    string OperationType,
    string EntityType,
    string? EntityId,
    AffidavitField[] Fields,
    float AggregateConfidence,
    string[] Warnings,
    bool RequiresConfirmation);
