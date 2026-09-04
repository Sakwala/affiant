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
/// value, the value it replaces, and the full
/// <see cref="ProvenanceChain"/> — the audit trail for this field's value.
///
/// <para>
/// A field the operation does <b>not</b> propose — untouched on an update, not applicable to the
/// operation — is <b>absent</b> from <see cref="Affidavit.Fields"/>; a proposed field whose
/// provenance is unknown is <b>present</b> and tagged <see cref="ProvenanceTag.Empty"/> at
/// confidence 0. The two are never confused, which is what makes the field list a statement of
/// intent a policy can read.
/// </para>
///
/// Matches framework specification §2.6.
/// </summary>
/// <param name="PreviousValue">
/// The stored value this replaces. Null on a create, and also on an update field the entity had no
/// stored value for; the two are distinguished by the Affidavit's
/// <see cref="Affidavit.OperationType"/>, not by the field. On an update the projection fills this
/// from the host's <see cref="Interfaces.IPreviousValueSource"/>.
/// </param>
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
/// The three confidence numbers an <see cref="Affidavit"/> carries, and the one place they are
/// computed.
///
/// <para>
/// <b>Why the aggregate is a minimum and not a mean.</b> A mean that first discards every
/// <see cref="ProvenanceSource.Empty"/> field lets a mostly-empty Affidavit report high confidence:
/// a ten-field record with nine unknown fields and one field at 1.0 scores a perfect 1.0. That is
/// the exact hole once provenance authorises writes, so the aggregate is the <b>minimum</b> over
/// every proposed field with an <c>Empty</c> field counting as 0 — making it 0 if and only if some
/// proposed field has unknown provenance.
/// </para>
///
/// <para>
/// <b>Why two companions.</b> A single safety number that reads 0 tells a reviewer nothing about
/// how much of the record is blank or how good the populated part is.
/// <see cref="PopulatedConfidence"/> answers the second question and <see cref="EmptyFieldCount"/>
/// the first. A host policy floor predicates on those two; the aggregate is the safety number a
/// fixture pins, and neither the framework nor a policy defines a threshold on it.
/// </para>
/// </summary>
/// <param name="AggregateConfidence">
/// The minimum confidence over every proposed field's current tag, with an
/// <see cref="ProvenanceSource.Empty"/> field counting as 0 whatever its tag says. 0 for an
/// Affidavit with no fields at all — it has nothing to swear to.
/// </param>
/// <param name="PopulatedConfidence">
/// The minimum confidence over the non-<see cref="ProvenanceSource.Empty"/> proposed fields, or
/// null when there are none. Null rather than 0: "there is nothing populated to be confident about"
/// is a different statement from "the populated fields are worthless", and a card showing 0 would
/// say the second.
/// </param>
/// <param name="EmptyFieldCount">
/// How many proposed fields carry an <see cref="ProvenanceSource.Empty"/> current tag.
/// </param>
public readonly record struct AffidavitConfidence(
    float AggregateConfidence,
    float? PopulatedConfidence,
    int EmptyFieldCount)
{
    /// <summary>
    /// Compute the three numbers over <paramref name="fields"/>.
    ///
    /// This is the framework's one implementation: the schema-driven projection computes the
    /// numbers at filing time with it, and the amendment path recomputes them with it after an
    /// accepted correction, so a card can never show a number that was about a different set of
    /// values.
    /// </summary>
    public static AffidavitConfidence Compute(IReadOnlyList<AffidavitField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        float? aggregate = null;
        float? populated = null;
        var emptyFieldCount = 0;

        foreach (var field in fields)
        {
            var tag = field.Provenance.Current;
            var isEmpty = tag.Source == ProvenanceSource.Empty;
            var contribution = isEmpty ? 0f : tag.Confidence;

            aggregate = aggregate is null ? contribution : Math.Min(aggregate.Value, contribution);

            if (isEmpty)
                emptyFieldCount++;
            else
                populated = populated is null ? tag.Confidence : Math.Min(populated.Value, tag.Confidence);
        }

        return new AffidavitConfidence(aggregate ?? 0f, populated, emptyFieldCount);
    }
}

/// <summary>
/// The sworn evidence report for a proposed mutation. Every proposed write
/// (create, update, delete) flows through an Affidavit, carrying full provenance
/// for every field.
///
/// The <see cref="EntityType"/> + <see cref="EntityId"/> pair identifies which domain
/// entity is being mutated; <see cref="EntityId"/> is null for create operations and non-null for
/// updates, which makes "create-only" a predicate a policy can test.
///
/// Matches framework specification §2.6.
/// </summary>
/// <param name="Fields">
/// Exactly the fields the operation proposes: every proposed field is present — tagged
/// <see cref="ProvenanceTag.Empty"/> when its provenance is unknown — and no other field is.
/// </param>
/// <param name="AggregateConfidence">
/// The <b>minimum</b> confidence over every proposed field, with an
/// <see cref="ProvenanceSource.Empty"/> field counting as 0 — see
/// <see cref="AffidavitConfidence"/> for why it is not a mean, and
/// <see cref="AffidavitConfidence.Compute"/> for the one place it is computed.
/// </param>
/// <param name="PopulatedConfidence">
/// The minimum confidence over the non-<see cref="ProvenanceSource.Empty"/> proposed fields, or
/// null when there are none.
/// </param>
/// <param name="EmptyFieldCount">How many proposed fields carry an <c>Empty</c> current tag.</param>
/// <param name="ConversationTurn">
/// The conversation turn the proposal was made on, or <see langword="null"/> when it did not come
/// from a turn. It is the record's own turn, and it is what a reviewer's accepted amendment is
/// dated to: a correction belongs to the conversation the proposal was made in, never to the turn
/// on the tag it displaces, which says when the machine produced the value being replaced.
/// </param>
/// <param name="CreatedAt">
/// When this Affidavit was built. Passed in by whoever built it — the gate stamps its own clock —
/// and never read from a clock inside the model, so a fixture can pin it.
/// </param>
/// <param name="ProtocolVersion">
/// The protocol version this record conforms to (SR-3). Every envelope that crosses the wire says
/// which version it speaks, and a record is an envelope.
/// </param>
public sealed record Affidavit(
    string OperationType,
    string EntityType,
    string? EntityId,
    AffidavitField[] Fields,
    float AggregateConfidence,
    float? PopulatedConfidence,
    int EmptyFieldCount,
    string[] Warnings,
    bool RequiresConfirmation,
    int? ConversationTurn = null,
    DateTimeOffset? CreatedAt = null,
    string ProtocolVersion = AffiantProtocol.Version)
{
    /// <summary>
    /// Build an Affidavit with all three confidence numbers computed from
    /// <paramref name="fields"/> — the way every producer should build one, so a hand-written
    /// aggregate can never disagree with the fields it is supposed to summarise.
    /// </summary>
    public static Affidavit Create(
        string operationType,
        string entityType,
        string? entityId,
        AffidavitField[] fields,
        string[] warnings,
        bool requiresConfirmation = true,
        int? conversationTurn = null,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(warnings);

        var confidence = AffidavitConfidence.Compute(fields);
        return new Affidavit(
            operationType,
            entityType,
            entityId,
            fields,
            confidence.AggregateConfidence,
            confidence.PopulatedConfidence,
            confidence.EmptyFieldCount,
            warnings,
            requiresConfirmation,
            conversationTurn,
            createdAt);
    }

    /// <summary>
    /// This Affidavit with <paramref name="fields"/> in place of <see cref="Fields"/> and all three
    /// confidence numbers recomputed over them — what an accepted amendment returns, so a corrected
    /// card never reports the machine's pre-correction confidence.
    /// </summary>
    public Affidavit WithFields(AffidavitField[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var confidence = AffidavitConfidence.Compute(fields);
        return this with
        {
            Fields = fields,
            AggregateConfidence = confidence.AggregateConfidence,
            PopulatedConfidence = confidence.PopulatedConfidence,
            EmptyFieldCount = confidence.EmptyFieldCount,
        };
    }
}
