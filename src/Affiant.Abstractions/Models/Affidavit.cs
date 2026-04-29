namespace Affiant.Abstractions.Models;

/// <summary>
/// A single sworn field inside an <see cref="Affidavit"/>. Carries the proposed
/// value, the previous value (null for create operations), and the full
/// <see cref="ProvenanceChain"/> — the audit trail for this field's value.
///
/// Matches framework specification §2.6.
/// </summary>
public sealed record AffidavitField(
    string Name,
    object? Value,
    object? PreviousValue,
    ProvenanceChain Provenance);

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
