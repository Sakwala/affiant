using System.Text.Json.Serialization;

namespace Affiant.Abstractions.Models;

/// <summary>
/// The provenance sources an <see cref="ProvenanceTag"/> field can originate from.
///
/// Values are ordered from most deterministic to least. This ordering doubles as the
/// determinism hierarchy for confidence-tie merge rules: when two tags carry equal
/// confidence, the one with the lower integer ordinal wins.
///
/// Full rationale lives in the framework specification §2.1.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProvenanceSource
{
    /// <summary>
    /// The user explicitly stated this value. Maximal confidence.
    /// </summary>
    UserStated,

    /// <summary>
    /// Fetched from an authoritative external system (API lookup, database read,
    /// third-party service response).
    /// </summary>
    External,

    /// <summary>
    /// Derived by deterministic business logic (tax calculation, date math,
    /// priority-based SLA computation).
    /// </summary>
    Computed,

    /// <summary>
    /// Mentioned in conversation context through a tool result but not directly
    /// stated as a value by the user.
    /// </summary>
    Conversation,

    /// <summary>
    /// LLM-inferred from conversational signals. Requires reviewer confirmation
    /// before committing.
    /// </summary>
    Inferred,

    /// <summary>
    /// System default or fallback applied when no conversational basis exists.
    /// </summary>
    Default,

    /// <summary>
    /// Provenance unknown. MUST be explicitly tagged rather than omitted —
    /// missing provenance is indistinguishable from "the framework forgot to
    /// track it". See framework spec §6 Rule 7.
    /// </summary>
    Empty
}

/// <summary>
/// The two grades an implementation's own inference is allowed to mint.
///
/// <para>
/// This type exists so the restriction is <b>structural</b> rather than a convention a reviewer has
/// to spot: the inference path is handed
/// <see cref="ProvenanceTag.FromInference(InferenceSource, string, float, ProvenanceBinding?)"/>,
/// whose source parameter is this enum and therefore cannot name
/// <see cref="ProvenanceSource.UserStated"/>, <see cref="ProvenanceSource.External"/> or
/// <see cref="ProvenanceSource.Computed"/>.
/// </para>
///
/// <para>
/// <see cref="ProvenanceSource.UserStated"/> is an observation of a person's act — an utterance
/// span, a form input, a reviewer's amendment or prefill — never the host vouching for a value it
/// produced itself. <see cref="ProvenanceSource.External"/> and
/// <see cref="ProvenanceSource.Computed"/> claim an artifact outside the conversation, which an
/// inference has none of.
/// </para>
/// </summary>
public enum InferenceSource
{
    /// <summary>The value was literally present in the unmodified turn.</summary>
    Conversation,

    /// <summary>The model reasoned to the value from conversational signals.</summary>
    Inferred
}

/// <summary>
/// Sworn-provenance tag carried by every field value the framework tracks.
/// Records where a value came from (<see cref="Source"/>), how confident
/// the framework is in it (<see cref="Confidence"/>), a human-readable
/// explanation (<see cref="Evidence"/>), which conversation turn produced it
/// (<see cref="ConversationTurn"/>), and — for a grade that claims an artifact outside the
/// conversation — what an auditor should look at to check it (<see cref="Binding"/>).
///
/// Matches framework specification §2.2.
/// </summary>
/// <param name="Source">Where the value came from.</param>
/// <param name="Confidence">
/// Confidence in the value. <b>Always clamped into <c>[0, 1]</c> by this record</b>, and always
/// <c>0</c> when <paramref name="Source"/> is <see cref="ProvenanceSource.Empty"/> — "nobody knows
/// where this came from" cannot also be a confident claim. A producer that reports 1.4, -0.2 or
/// <see cref="float.NaN"/> gets 1, 0 and 0 respectively; the clamp lives here rather than at each
/// mint site so no caller can route around it.
/// </param>
/// <param name="Evidence">
/// A human-readable line for the reviewer, or null when there is nothing to say.
///
/// <b>Spelled <c>note</c> on the wire</b> (SR-3): the whole record is the evidence, and this
/// property is the one sentence a person reads. The seed wire called it <c>evidence</c>; the v0.1
/// schema renames it. The CLR name stays <c>Evidence</c> so existing .NET call sites compile
/// unchanged.
/// </param>
/// <param name="ConversationTurn">Index of the turn the value came from, or null.</param>
/// <param name="At">
/// When the tag was minted, or null when the producer did not stamp one.
///
/// <para>
/// New in v0.1: the seed wire had nowhere to put it, so a chain read off the seed could not say
/// <i>when</i> a claim was made — and a provenance chain whose tags cannot be placed in time is a
/// history a reader cannot order. The v0.1 schema requires it on every tag.
/// </para>
///
/// <para>
/// <b>Nullable, and null at every framework mint site in this change.</b> Stamping it means reading
/// a clock, and the framework's one time seam — <c>TimeProvider</c> injected into the gate and the
/// Docket stores — arrives in a separate change that this one is not stacked on. The one place a
/// tag is minted with an instant already in hand is a reviewer's accepted amendment, whose
/// <c>decisionAt</c> is passed in rather than read from a clock, and that tag carries it (see
/// <see cref="AffidavitAmendments.AmendmentTag"/>). Every other mint site stamps null until the
/// clock seam lands, at which point they take it from the injected provider rather than from
/// <c>DateTimeOffset.UtcNow</c>.
/// </para>
/// </param>
/// <param name="Binding">
/// What to look at to check the value, or null when the producer had nothing to point at. A tag
/// graded above <see cref="ProvenanceSource.Conversation"/> should carry one — see
/// <see cref="ProvenanceBinding"/> and <see cref="IsBound"/>.
/// </param>
public sealed record ProvenanceTag(
    ProvenanceSource Source,
    float Confidence,
    [property: JsonPropertyName("note")] string? Evidence,
    int? ConversationTurn,
    ProvenanceBinding? Binding = null,
    DateTimeOffset? At = null)
{
    private readonly float _confidence = Normalize(Source, Confidence);

    /// <inheritdoc cref="ProvenanceTag(ProvenanceSource, float, string?, int?, ProvenanceBinding?, DateTimeOffset?)" />
    public float Confidence
    {
        get => this.Source == ProvenanceSource.Empty ? 0f : _confidence;
        init => _confidence = Normalize(this.Source, value);
    }

    /// <summary>
    /// Clamps a producer-reported confidence into <c>[0, 1]</c> and forces an
    /// <see cref="ProvenanceSource.Empty"/> tag to 0.
    ///
    /// <see cref="float.NaN"/> becomes 0: a number that is not a number is not a claim.
    /// </summary>
    private static float Normalize(ProvenanceSource source, float confidence)
    {
        if (source == ProvenanceSource.Empty) return 0f;
        if (float.IsNaN(confidence)) return 0f;
        return Math.Clamp(confidence, 0f, 1f);
    }

    /// <summary>
    /// Whether this tag points at something an auditor can check — see <see cref="ProvenanceBinding"/>.
    /// </summary>
    [JsonIgnore]
    public bool IsBound => Binding is not null;

    /// <summary>
    /// Whether a tag with this source should carry a <see cref="ProvenanceBinding"/> to be worth its
    /// grade: the three sources <b>above</b> <see cref="ProvenanceSource.Conversation"/> —
    /// <see cref="ProvenanceSource.UserStated"/>, <see cref="ProvenanceSource.External"/> and
    /// <see cref="ProvenanceSource.Computed"/>.
    ///
    /// At or below <see cref="ProvenanceSource.Conversation"/> the grade already says "this came
    /// from the turn, or from a model reading the turn", and the turn is itself the artifact. Above
    /// it, the tag claims an artifact outside the conversation, and a claim with no pointer at that
    /// artifact is not checkable.
    /// </summary>
    public static bool RequiresBinding(ProvenanceSource source) =>
        (int)source < (int)ProvenanceSource.Conversation;

    /// <summary>
    /// Whether this tag wins a merge against <paramref name="incumbent"/>: higher confidence wins,
    /// and a tie breaks toward the more deterministic source (the lower
    /// <see cref="ProvenanceSource"/> ordinal).
    ///
    /// <para>
    /// An exact tie — same confidence, same source — leaves the incumbent in force. It was there
    /// first and the challenger brings nothing new; the challenger is still preserved in the chain,
    /// so the fact that two producers agreed stays on the record.
    /// </para>
    ///
    /// <para>
    /// This is the framework's one implementation of the merge comparison. Everything that has to
    /// decide which of two tags wins — <see cref="ProvenanceChain.Merge"/>, the schema-driven
    /// projection, the task-inference merge step — calls it, so the rule cannot be stated three
    /// slightly different ways.
    /// </para>
    ///
    /// <para>
    /// A reviewer's act is deliberately <em>not</em> routed through here: it is not a confidence
    /// contest it might lose. See <see cref="ProvenanceChain.Append"/>.
    /// </para>
    /// </summary>
    public bool Beats(ProvenanceTag incumbent)
    {
        ArgumentNullException.ThrowIfNull(incumbent);

        if (Confidence > incumbent.Confidence) return true;
        if (Confidence < incumbent.Confidence) return false;
        if ((int)Source < (int)incumbent.Source) return true;
        if ((int)Source > (int)incumbent.Source) return false;

        // Equal confidence, equal grade: the tag that points at something an auditor can go and
        // check displaces the one that points at nothing (PV-2, PV-3). A value read out of an
        // utterance span is more evidence than the same value as an unbound literal, and a rule that
        // treated them as a tie would keep whichever happened to be tagged first.
        return Binding is not null && incumbent.Binding is null;
    }

    /// <summary>
    /// The canonical "no data" tag. Every field with no known provenance must be
    /// tagged with this value rather than left unset — see framework spec §6 Rule 7.
    /// </summary>
    public static ProvenanceTag Empty { get; } = new(ProvenanceSource.Empty, 0f, null, null);

    /// <summary>
    /// Tag a value extracted from a deterministic tool RESULT — what a search or a lookup returned
    /// about the conversation's subject. Default confidence 0.9.
    /// </summary>
    /// <remarks>
    /// Not for the arguments a model passes to a write tool. Those are not provenance at all: an
    /// argument is the value the model proposes, and what is sworn about where it came from is
    /// whatever an interceptor or the inference port says — nothing, if neither speaks.
    /// </remarks>
    public static ProvenanceTag FromTool(string toolName, float confidence = 0.9f) =>
        new(ProvenanceSource.Conversation, confidence, $"Extracted from {toolName}", null);

    /// <summary>
    /// Tag a value the implementation's own inference produced.
    ///
    /// <para>
    /// <paramref name="source"/> is an <see cref="InferenceSource"/> and not a
    /// <see cref="ProvenanceSource"/>, which is the whole point: an inference can say "the value was
    /// literally in the turn" (<see cref="InferenceSource.Conversation"/>) or "the model reasoned to
    /// it" (<see cref="InferenceSource.Inferred"/>) and has no way at all to claim
    /// <see cref="ProvenanceSource.UserStated"/>, <see cref="ProvenanceSource.External"/> or
    /// <see cref="ProvenanceSource.Computed"/>. The restriction is a property of the type rather
    /// than a convention a reviewer has to spot.
    /// </para>
    /// </summary>
    /// <param name="source">Which of the two inference grades is being claimed.</param>
    /// <param name="fieldName">The field the value belongs to, for the reviewer-facing line.</param>
    /// <param name="confidence">
    /// The model's reported confidence. Clamped into <c>[0, 1]</c> by the tag itself.
    /// </param>
    /// <param name="binding">
    /// An <see cref="ProvenanceBinding.UtteranceSpan"/> when the inference port supplied offsets
    /// into the unmodified utterance; null otherwise.
    /// </param>
    public static ProvenanceTag FromInference(
        InferenceSource source,
        string fieldName,
        float confidence = 0.6f,
        ProvenanceBinding? binding = null,
        DateTimeOffset? at = null) =>
        new(
            source == InferenceSource.Conversation
                ? ProvenanceSource.Conversation
                : ProvenanceSource.Inferred,
            confidence,
            // The two sentences are the protocol's, not this framework's phrasing: a tag's note is
            // part of the record the canonical form is taken over, so two implementations that
            // worded it differently could never produce the same hash for the same facts and no
            // execution grant minted by one would validate against the other (SR-1).
            source == InferenceSource.Conversation
                ? $"Literally present in the turn: {fieldName}"
                : $"Inferred from the turn: {fieldName}",
            null,
            binding,
            at);

    /// <summary>
    /// Tag a value applied by a deterministic fallback rule. Default confidence 0.3.
    /// </summary>
    public static ProvenanceTag FromDefault(string reason, float confidence = 0.3f) =>
        new(ProvenanceSource.Default, confidence, reason, null);

    /// <summary>
    /// Tag a value a person stated: an utterance span, a form input, a reviewer's amendment or
    /// prefill. Maximal confidence.
    /// </summary>
    /// <param name="fieldName">The field the person stated, for the reviewer-facing line.</param>
    /// <param name="binding">
    /// What an auditor should look at to check the claim — an
    /// <see cref="ProvenanceBinding.UtteranceSpan"/>, a <see cref="ProvenanceBinding.FormInput"/>
    /// or a <see cref="ProvenanceBinding.ReviewerAct"/>. Pass <c>null</c> only when the caller
    /// genuinely has nothing to point at: an unbound <see cref="ProvenanceSource.UserStated"/> tag
    /// is recorded exactly as claimed, but it is the weakest form of the strongest grade and a
    /// policy is entitled to refuse to rest on it.
    /// </param>
    public static ProvenanceTag FromUser(string fieldName, ProvenanceBinding? binding) =>
        new(ProvenanceSource.UserStated, 1.0f, $"User stated: {fieldName}", null, binding);
}
