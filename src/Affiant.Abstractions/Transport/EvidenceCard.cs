namespace Affiant.Abstractions.Transport;

using System.Text.Json.Serialization;
using Affiant.Abstractions;
using Affiant.Abstractions.Models;

/// <summary>Reviewer's decision on an EvidenceCardRequest.</summary>
public enum ApprovalDecision
{
    Approved,
    Rejected
}

/// <summary>
/// Payload sent to the UI when a WriteProposal enters the review queue.
/// Transported via <see cref="TransportEvent.EvidenceCardRequest"/>.
/// </summary>
/// <remarks>
/// <b>At-least-once delivery, no receipt guarantee (Area-5 Decision 3, affiant#28).</b>
/// <see cref="Interfaces.IStreamingTransport.BroadcastToGroupAsync"/> only reports whether the
/// underlying send call completed, never whether a human received or rendered the card — a session
/// group with zero currently-connected members completes the broadcast successfully with zero
/// recipients. The framework compensates by re-broadcasting this event for every entry still
/// <see cref="ReviewStatus.Pending"/> on every 30-second <c>DocketExpiryService</c> sweep
/// tick, and again on session reconnect via <c>ReviewGate.RebroadcastPendingCardsAsync</c>, until the
/// entry is acted on or expires — the same idempotent-repeat contract
/// <see cref="DocketExpiringNotification"/> already documents for <c>DocketExpiring</c>, applied here
/// to the card itself. Clients MUST treat a repeated <c>EvidenceCardRequest</c> for the same
/// <see cref="DocketId"/> as idempotent — render or update the existing card in place, never append a
/// duplicate. This closes "the client gets the card again on reconnect/next sweep tick until it acts
/// or the entry expires"; it does NOT prove a human ever saw the card — that stronger guarantee would
/// need a separate, costed client-ack RPC and is explicitly out of scope here (Area-5 D3 research
/// pack, §1/§5 criterion 7).
/// </remarks>
/// <param name="PriorAmendments">
/// Set only when this Evidence Card is a resubmission of a previously expired review (framework
/// half of repo issue #9) — carries the amendments a reviewer made on the original, expired entry
/// before the window lapsed, so the new reviewer can see what was already agreed. <c>null</c> for
/// a first-time filing. A <c>null</c> <em>under a key</em> inside the map means the reviewer
/// <b>cleared</b> that field, which is a different statement from the key being absent (DK-2).
/// </param>
/// <param name="PopulatedConfidence">
/// The minimum confidence over the non-<c>Empty</c> proposed fields, or null when there are none
/// (AF-2). The same number <see cref="Models.Affidavit.PopulatedConfidence"/> carries, repeated here
/// because <b>a card shows all three numbers</b> and this is where the seed put the two companions,
/// so a consumer written against either shape finds them.
/// </param>
/// <param name="EmptyFieldCount">
/// How many proposed fields are tagged <c>Empty</c> (AF-2). The same number the Affidavit carries;
/// repeated here for the same reason as <paramref name="PopulatedConfidence"/>.
/// </param>
/// <param name="RequiresConfirmation">
/// Whether a person must confirm this write before it commits. The <b>policy chain's verdict</b>,
/// not a property of the evidence, which is why it belongs on the envelope. False on a blocked
/// entry: a card carrying a marker that says no decision will be accepted must not also offer a
/// reviewer surface an approve button that cannot work.
/// </param>
/// <param name="Blocked">
/// Why no decision on this entry will be accepted, or null when it can be decided (AZ-4, CV-4).
/// Structured so a reviewer surface can render it, rather than left for a client to infer from the
/// text of a warning. Null until the Docket row carries a blocked column — see
/// <see cref="BlockedMarker"/> for what that change is and why the type is declared already.
/// </param>
/// <param name="Presentation">
/// How a reviewer surface should render each field's input: the hints the host's inference strategy
/// declared, lifted onto the envelope. One entry per field that has a hint, naming a field the
/// Affidavit carries; omitted entirely when there are none.
///
/// <para>
/// <b>Presentation, not substance.</b> The gate carries a hint and validates nothing against it: a
/// proposed or amended value outside a declared <see cref="FieldPresentation.AllowedValues"/> set is
/// still recorded, and a <see cref="FieldPresentation.Pattern"/> is carried verbatim and never
/// compiled or applied. It lives on the envelope for the same reason
/// <paramref name="RequiresConfirmation"/> does — the core swears to what a value is and where it
/// came from, not to how it should be shown — and nothing here is part of the canonical form, which
/// is defined over the Affidavit and its accepted amendments alone (SR-1).
/// </para>
/// </param>
/// <param name="Warnings">
/// Sentences a reviewer should see beside the record: the reason a policy gave for its verdict, and
/// the sentence a blocked entry shows. Omitted when there are none. Presentation, not substance —
/// the machine-readable half of a blocked entry is <paramref name="Blocked"/>, and a consumer never
/// switches on the text of a warning.
/// </param>
/// <param name="HostOperation">
/// The host's own verb for the operation — "WriteUpdate", "Reprice", "Onboard" — omitted when the
/// host named none. Carried beside <see cref="Models.Affidavit.OperationType"/>, never instead of
/// it: a reviewer surface can head the card with the term a person recognises, while a policy still
/// tests the protocol's own shape vocabulary. Presentation, and never part of the canonical form
/// (SR-1): a host that renames a verb has not changed the evidence.
/// </param>
public record EvidenceCardRequest(
    Guid DocketId,
    Affidavit Affidavit,
    DateTimeOffset RequiredBy,
    IReadOnlyDictionary<string, object?>? PriorAmendments = null,
    float? PopulatedConfidence = null,
    int EmptyFieldCount = 0,
    bool RequiresConfirmation = true,
    BlockedMarker? Blocked = null,
    IReadOnlyList<FieldPresentation>? Presentation = null,
    IReadOnlyList<string>? Warnings = null,
    string? HostOperation = null)
{
    /// <summary>
    /// The protocol version this envelope conforms to (SR-4) — always
    /// <see cref="AffiantProtocol.Version"/>. Init-only rather than a constructor parameter: an
    /// envelope's version is a property of the build that emitted it, never a caller's choice.
    /// </summary>
    public string ProtocolVersion { get; init; } = AffiantProtocol.Version;

    /// <inheritdoc cref="EvidenceCardRequest(Guid, Affidavit, DateTimeOffset, IReadOnlyDictionary{string, object}, float?, int, bool, BlockedMarker, IReadOnlyList{FieldPresentation}, IReadOnlyList{string}, string)" />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FieldPresentation>? Presentation { get; init; } = Presentation;

    /// <inheritdoc cref="EvidenceCardRequest(Guid, Affidavit, DateTimeOffset, IReadOnlyDictionary{string, object}, float?, int, bool, BlockedMarker, IReadOnlyList{FieldPresentation}, IReadOnlyList{string}, string)" />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Warnings { get; init; } = Warnings;

    /// <inheritdoc cref="EvidenceCardRequest(Guid, Affidavit, DateTimeOffset, IReadOnlyDictionary{string, object}, float?, int, bool, BlockedMarker, IReadOnlyList{FieldPresentation}, IReadOnlyList{string}, string)" />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostOperation { get; init; } = HostOperation;

    /// <summary>
    /// Build the card for <paramref name="affidavit"/>, lifting everything the envelope repeats from
    /// the record itself: the two companion confidence numbers (AF-2), the warnings, whether a
    /// person must confirm, and the per-field presentation hints.
    ///
    /// <para>
    /// This is the way to build one. Constructing the record directly and passing the numbers by
    /// hand is how a card ends up reporting a confidence that is about a different set of values
    /// than the ones it displays — the exact defect AF-2 and AF-4 exist to close.
    /// </para>
    /// </summary>
    /// <param name="docketId">The Docket entry this card is filed under.</param>
    /// <param name="affidavit">The record awaiting a decision: the proposal, or the state an accepted amendment produced.</param>
    /// <param name="requiredBy">When the review window closes — the entry's own expiry (GT-4).</param>
    /// <param name="priorAmendments">The amendments made on a superseded entry, or null on a first filing.</param>
    /// <param name="blocked">Why no decision will be accepted, or null when the entry can be decided.</param>
    /// <param name="hostOperation">The host's own verb for the operation, or null when it named none.</param>
    public static EvidenceCardRequest For(
        Guid docketId,
        Affidavit affidavit,
        DateTimeOffset requiredBy,
        IReadOnlyDictionary<string, object?>? priorAmendments = null,
        BlockedMarker? blocked = null,
        string? hostOperation = null)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        var presentation = affidavit.Fields
            .Select(FieldPresentation.For)
            .Where(hint => hint is not null)
            .Select(hint => hint!)
            .ToArray();

        return new EvidenceCardRequest(
            docketId,
            affidavit,
            requiredBy,
            priorAmendments,
            affidavit.PopulatedConfidence,
            affidavit.EmptyFieldCount,
            // A blocked entry never claims a confirmation is being awaited.
            RequiresConfirmation: blocked is null && affidavit.RequiresConfirmation,
            blocked,
            presentation.Length == 0 ? null : presentation,
            affidavit.Warnings.Length == 0 ? null : affidavit.Warnings,
            hostOperation);
    }
}

/// <summary>
/// How a reviewer surface should render one field's input — the rendering hints a host's inference
/// strategy declared, carried beside the Affidavit rather than on it.
///
/// <para>
/// Presentation, sworn to by nobody: the gate validates no value against any of it, and none of it
/// is part of the canonical form (SR-1). A host that changes an input mask has not changed the
/// evidence.
/// </para>
/// </summary>
/// <param name="Name">The field these hints are about — a name present in the Affidavit's fields.</param>
/// <param name="Kind">
/// The rendering hint the field carries, repeated here so a surface reading only this array has it.
/// One of the <see cref="AffidavitFieldKind"/> constants. The Affidavit's own copy is the one the
/// record swears to.
/// </param>
/// <param name="AllowedValues">
/// The closed set an amendment input offers, in the order a surface should show them. Omitted when
/// the host declared none. A value outside the set is still recorded — the set is a hint to the
/// control, not a constraint on the record.
/// </param>
/// <param name="Pattern">
/// The regular expression an amendment input is constrained by, as the host wrote it. Omitted when
/// the host declared none. Carried verbatim and never compiled or applied by the gate; a host that
/// wants a value refused enforces that in its own policy.
/// </param>
public sealed record FieldPresentation(
    string Name,
    string? Kind = null,
    IReadOnlyList<object?>? AllowedValues = null,
    string? Pattern = null)
{
    /// <inheritdoc cref="FieldPresentation(string, string, IReadOnlyList{object}, string)" />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; } = Kind;

    /// <inheritdoc cref="FieldPresentation(string, string, IReadOnlyList{object}, string)" />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<object?>? AllowedValues { get; init; } = AllowedValues;

    /// <inheritdoc cref="FieldPresentation(string, string, IReadOnlyList{object}, string)" />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pattern { get; init; } = Pattern;

    /// <summary>
    /// The hints <paramref name="field"/> declares, or <c>null</c> when it declares none worth
    /// sending — a plain text field with no closed set and no pattern needs no entry, and an empty
    /// array of hints is noise on every card.
    /// </summary>
    public static FieldPresentation? For(AffidavitField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var hasHints =
            !string.Equals(field.Kind, AffidavitFieldKind.Text, StringComparison.Ordinal) ||
            field.AllowedValues is { Count: > 0 } ||
            field.Pattern is not null;

        return hasHints
            ? new FieldPresentation(
                field.Name,
                field.Kind,
                field.AllowedValues is { Count: > 0 } values ? [.. values] : null,
                field.Pattern)
            : null;
    }
}

/// <summary>
/// Payload returned by the UI after the reviewer acts on an Evidence Card.
/// Transported via <see cref="TransportEvent.EvidenceCardResponse"/>.
///
/// <see cref="Amendments"/> carries the fields the reviewer edited before approving —
/// keyed by <see cref="AffidavitField.Name"/>, values are the reviewer's replacement
/// value (<c>null</c> means the reviewer explicitly cleared the field). Null or empty on
/// rejection, or when the reviewer approved without editing anything. The framework's review
/// gate service persists these onto the <see cref="DocketEntry"/> it owns <em>and</em> folds them
/// into an amended <see cref="Affidavit"/> beside the proposal — the reviewer's act on each amended
/// field's chain and the three confidence numbers recomputed — returned on
/// <see cref="ReviewOutcome.Approved.AmendedAffidavit"/>. See
/// <see cref="AffidavitAmendments.Apply"/>, which is what a host's
/// <see cref="Interfaces.IWriteExecutor"/> should use rather than stamping tags by hand.
/// </summary>
public record EvidenceCardResponse(
    Guid DocketId,
    ApprovalDecision Decision,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Amendments = null)
{
    /// <summary>
    /// Who the gate held this decision to, carried in-process from the call that received it to the
    /// call that writes the row (AZ-1). Never on the wire, and never something a client supplies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reviewer's client sends a decision, not an identity — a client that could name whose
    /// signature a decision is would be the whole problem. The gate resolves the principal itself,
    /// builds the attestation from it and from nothing else, and puts it here on its way to
    /// whichever call performs the transition: the awaiting <c>ReviewGate.FileReviewAsync</c> when
    /// one is holding the entry open, and the deciding call otherwise. Either way the row that gets
    /// written carries the attestation, because a decided row that cannot name who agreed is not a
    /// record.
    /// </para>
    /// <para>
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/> is the enforcement: this
    /// property is a hand-off inside one process and is neither serialized to a client nor read from
    /// one, so an attestation can only ever be the one the gate computed.
    /// </para>
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public Attestation? Attestation { get; init; }
}
