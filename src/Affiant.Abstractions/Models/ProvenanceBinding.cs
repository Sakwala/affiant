using System.Text.Json.Serialization;

namespace Affiant.Abstractions.Models;

/// <summary>
/// What to look at to check a value.
///
/// <para>
/// A <see cref="ProvenanceTag"/> says where a value came from; a binding points at the artifact an
/// auditor can go and check years later. The five kinds below are a <b>fixed set</b> — a binding
/// kind nobody can enumerate is a binding nobody can audit — and a binding whose source cannot be
/// re-fetched or re-verified is not a binding.
/// </para>
///
/// <para>
/// A tag with no binding is not a lie, it is a weaker claim, and the framework's job is to keep the
/// difference visible rather than to average it away: a tag graded above
/// <see cref="ProvenanceSource.Conversation"/> with no binding is recorded exactly as it was
/// claimed, and it is a separate question — one a policy asks, not this type — whether a verdict
/// reached with no person present may rest on it.
/// </para>
///
/// <para>
/// <b>On the wire</b> a binding is <c>{ "kind": …, "ref": { … } }</c>: the discriminator is
/// <c>kind</c>, spelled in kebab-case, and the payload always sits under <c>ref</c>. Both names are
/// pinned with explicit attributes rather than left to a host's naming policy, because the same
/// bytes have to read the same way whichever transport carried them.
/// </para>
/// </summary>
/// <para>
/// The converter, rather than <c>[JsonPolymorphic]</c>, because a binding does not always arrive
/// with its discriminator first — PostgreSQL's <c>jsonb</c>, the column type the Docket stores an
/// Affidavit in, sorts an object's keys and returns <c>ref</c> before <c>kind</c> — and the
/// built-in polymorphic reader refuses such an object as having no discriminator at all. See
/// <see cref="Serialization.ProvenanceBindingConverter"/>.
/// </para>
[JsonConverter(typeof(Serialization.ProvenanceBindingConverter))]
public abstract record ProvenanceBinding
{
    /// <summary>
    /// The kind discriminator this binding travels under, as it appears on the wire.
    /// Reading it never requires a type test.
    /// </summary>
    [JsonIgnore]
    public abstract string Kind { get; }

    /// <summary>
    /// The span of the unmodified utterance the value was read from.
    ///
    /// Offset and length rather than a start/end pair, and a hash of the substring: offsets alone
    /// rot the moment anything re-wraps or re-encodes a transcript, so the hash is what lets an
    /// auditor prove the span still says what it said.
    /// </summary>
    public sealed record UtteranceSpan(
        [property: JsonPropertyName("ref")] UtteranceSpanRef Ref) : ProvenanceBinding
    {
        /// <inheritdoc />
        [JsonIgnore]
        public override string Kind => ProvenanceBindingKind.UtteranceSpan;
    }

    /// <summary>
    /// The Docket decision that amended or prefilled the field. A reviewer's correction is
    /// provenance in its own right: their act is what the new value rests on, and this names the
    /// act.
    /// </summary>
    public sealed record ReviewerAct(
        [property: JsonPropertyName("ref")] ReviewerActRef Ref) : ProvenanceBinding
    {
        /// <inheritdoc />
        [JsonIgnore]
        public override string Kind => ProvenanceBindingKind.ReviewerAct;
    }

    /// <summary>The form control a person typed into, as the host's own surface names it.</summary>
    public sealed record FormInput(
        [property: JsonPropertyName("ref")] FormInputRef Ref) : ProvenanceBinding
    {
        /// <inheritdoc />
        [JsonIgnore]
        public override string Kind => ProvenanceBindingKind.FormInput;
    }

    /// <summary>
    /// The system of record an <see cref="ProvenanceSource.External"/> value was read from.
    /// </summary>
    public sealed record ExternalRef(
        [property: JsonPropertyName("ref")] ExternalRecordRef Ref) : ProvenanceBinding
    {
        /// <inheritdoc />
        [JsonIgnore]
        public override string Kind => ProvenanceBindingKind.ExternalRef;
    }

    /// <summary>
    /// The deterministic rule a <see cref="ProvenanceSource.Computed"/> value came out of, and what
    /// it consumed.
    /// </summary>
    public sealed record ComputationRef(
        [property: JsonPropertyName("ref")] ComputationRuleRef Ref) : ProvenanceBinding
    {
        /// <inheritdoc />
        [JsonIgnore]
        public override string Kind => ProvenanceBindingKind.ComputationRef;
    }
}

/// <summary>
/// The five binding-kind discriminators, as string constants so a producer and a consumer
/// reference the same literals. Kebab-case is the wire spelling; nothing else is a binding kind.
/// </summary>
public static class ProvenanceBindingKind
{
    /// <summary>Discriminator for <see cref="ProvenanceBinding.UtteranceSpan"/>.</summary>
    public const string UtteranceSpan = "utterance-span";

    /// <summary>Discriminator for <see cref="ProvenanceBinding.ReviewerAct"/>.</summary>
    public const string ReviewerAct = "reviewer-act";

    /// <summary>Discriminator for <see cref="ProvenanceBinding.FormInput"/>.</summary>
    public const string FormInput = "form-input";

    /// <summary>Discriminator for <see cref="ProvenanceBinding.ExternalRef"/>.</summary>
    public const string ExternalRef = "external-ref";

    /// <summary>Discriminator for <see cref="ProvenanceBinding.ComputationRef"/>.</summary>
    public const string ComputationRef = "computation-ref";

    /// <summary>Every binding kind, in the order the rulebook lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
        [UtteranceSpan, ReviewerAct, FormInput, ExternalRef, ComputationRef];
}

/// <summary>
/// Where in the unmodified utterance a value was found: the character offset, the length, and a
/// digest of the spanned substring so the span can be checked after the fact.
/// </summary>
public sealed record UtteranceSpanRef(
    int Offset,
    int Length,
    string Hash);

/// <summary>
/// The Docket decision an amendment or a prefill was made on: which entry, and when.
/// </summary>
public sealed record ReviewerActRef(
    Guid EntryId,
    DateTimeOffset DecisionAt);

/// <summary>The form field a person typed into, named the way the host's surface names it.</summary>
public sealed record FormInputRef(string Field);

/// <summary>
/// A relay that asserted a person's identity rather than authenticating them: the channel the
/// capture arrived on and the message it arrived in.
/// </summary>
public sealed record RelayRef(
    string Principal,
    string ChannelIdentity,
    string MessageId);

/// <summary>
/// The system of record an <see cref="ProvenanceSource.External"/> value was read from.
///
/// <para>
/// <see cref="System"/> and <see cref="RecordId"/> are always present; the other three are present
/// only when the value's kind of source makes them checkable. <see cref="FetchedAt"/> and
/// <see cref="ContentHash"/> are what a value read from a published page with no API binds instead
/// of a stable record id: when it was read, and what it said when it was read.
/// <see cref="Relay"/> is present when the value arrived over a trusted relay.
/// </para>
/// </summary>
public sealed record ExternalRecordRef(
    string System,
    string RecordId,
    DateTimeOffset? FetchedAt = null,
    string? ContentHash = null,
    RelayRef? Relay = null);

/// <summary>
/// An externally published constant a computation consumed, and the date it was last verified.
///
/// When a value was checked is a different fact from when the tag was written: a rate table
/// verified in March and used in September is a September tag resting on a March fact, and a
/// reviewer is entitled to see both.
/// </summary>
public sealed record ComputationConstantRef(
    string Source,
    string VerifiedOn);

/// <summary>
/// The deterministic rule a <see cref="ProvenanceSource.Computed"/> value came out of. The rule is
/// re-runnable and named rather than described, and <see cref="Inputs"/> holds the field names it
/// consumed, in the order it consumed them.
/// </summary>
public sealed record ComputationRuleRef(
    string Rule,
    IReadOnlyList<string> Inputs,
    ComputationConstantRef? Constant = null);
