namespace Affiant.Abstractions.Models;

/// <summary>
/// Lifecycle states for a <see cref="DocketEntry"/>.
/// Ordering follows framework specification §2.7 — do not reorder.
/// </summary>
public enum ReviewStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
    /// <summary>Review delegated via Referral to another reviewer.</summary>
    Deferred
}

/// <summary>
/// A single step in the review history of an affidavit.
/// Populated as reviewers respond to a <see cref="DocketEntry"/>.
/// </summary>
public record ReviewStep(
    string ReviewerId,
    ReviewStatus Status,
    DateTimeOffset ReviewedAt,
    string? Comment = null);

/// <summary>
/// A pending <see cref="Affidavit"/> awaiting human review. The Docket is the
/// durable review queue; each entry is keyed by <see cref="EntryId"/>, a
/// <see cref="Guid"/> that doubles as the idempotency key for
/// <see cref="IDocketStore.UpdateReviewStatusAsync"/>'s optimistic concurrency guard.
///
/// <see cref="ReviewerUserId"/> is null when the entry is self-reviewed by the same
/// user who proposed it; set to a different user id for Referrals (delegated review).
/// <see cref="Amendments"/> records any fields the reviewer changed during approval — a
/// <c>null</c> value means the reviewer explicitly cleared that field, distinct from the
/// field being absent from the dictionary (unamended). Set at filing time from
/// <see cref="ReviewContext.Amendments"/> and, for the reviewer's actual edits captured on
/// the Evidence Card response, updated via <see cref="Interfaces.IDocketStore.UpdateAmendmentsAsync"/>.
///
/// <para>
/// <b>Residual risk (P1a, affiant#22 / FV-9):</b> this record has no field marking whether the
/// Evidence Card broadcast for a Pending entry ever succeeded — <c>ReviewGate</c> retries a failed
/// broadcast once and, on a second failure, logs + emits an OTel event rather than persisting a
/// marker here, because doing so would require an <see cref="IDocketStore"/> schema change (a new
/// column on every backend's entity + an EF migration). See <c>ReviewGate.BroadcastEvidenceCardWithRetryAsync</c>'s
/// remarks for the full reasoning. Area 5 (store reconciliation) owns closing this gap.
/// </para>
///
/// <para>
/// <b>Resubmission lineage (Area-5 Decision 2, affiant#31):</b> <see cref="ResubmittedTo"/> is set
/// exactly once, by <see cref="Interfaces.IDocketStore.ConsumeForResubmitAsync"/>, when this
/// entry — already <see cref="ReviewStatus.Expired"/> — is resubmitted for a fresh reviewer round.
/// It carries two facts in one field: the atomic race guard that stops two concurrent resubmissions
/// of the same entry from both minting a fresh <see cref="DocketEntry"/>, and the queryable answer
/// to "what did this become." There is deliberately no <c>ReviewStatus.Resubmitted</c> — <see cref="Status"/>
/// stays <see cref="ReviewStatus.Expired"/> on the source entry forever, matching the client's own
/// shipped decision to never visually distinguish a resubmitted card from a plain expired one. A
/// host reconciliation surface (e.g. status-polling after a reconnect) that wants to tell "this was
/// resubmitted" apart from "this just expired" checks <c>ResubmittedTo != null</c> in addition to
/// <see cref="Status"/> — see <c>ReviewGate.ResubmitAsync</c>'s remarks for the full guard/ordering
/// contract.
/// </para>
///
/// <para>
/// <b>D2 acceptance criterion 5 — reconciliation surfacing (open, not ruled by this wave):</b> the
/// d2 evidence pack's acceptance criteria ask whether a host's status-reporting surface (e.g. a
/// chat hub's client-facing status mapping — host code, not part of this repository) should map an
/// entry carrying a non-null <see cref="ResubmittedTo"/> to a distinct "resubmitted" wire value, or
/// explicitly rule that out. That decision has not been made;
/// do not assume either answer. This entry and <see cref="Interfaces.IDocketStore.GetResubmissionParentAsync"/>
/// already expose everything a host needs to build that surface once the ruling lands — the
/// framework's own <see cref="ReviewStatusExtensions.ToReviewOutcome"/> mapping does not surface it
/// today (see that method's remarks), so a host cannot get it "for free" from this repository
/// without the host-wave decision this note exists to keep visible.
/// </para>
///
/// Matches framework specification §2.7.
/// </summary>
public sealed record DocketEntry(
    Guid EntryId,
    string SessionId,
    string TenantId,
    string UserId,
    [property: Obsolete(
        "Who decided an entry is recorded in DocketEntry.Attestation, which names the person, the " +
        "relay that carried their decision, or the Standing Order that fired — ReviewerUserId can " +
        "say only the first and cannot say how the claim was made. Kept as an alias for one " +
        "release; read Attestation instead.",
        error: false,
        DiagnosticId = "AFFIANT0001")]
    string? ReviewerUserId,
    string OperationType,
    Affidavit Envelope,
    ReviewStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, object?>? Amendments,
    Guid? ResubmittedTo = null,

    // ── The facts a row accumulates after filing ─────────────────────────────
    // Every member below is a LATER FACT appended beside what was already there — never an edit of
    // a recorded one. They carry defaults so a caller that constructs the twelve original
    // parameters positionally still compiles: what those callers get is a freshly filed row with
    // no decision, no attestation and no execution outcome, which is exactly what a filing is.
    // What became of the write. Non-null exactly when Status is
    // Approved: an approved-but-failed write must stay distinguishable
    // from an approved-and-committed one. Recorded once, under a guarded transition from
    // Unexecuted — see
    // RecordExecutionAsync.
    ExecutionOutcome? Execution = null,

    // What the executor reported, or null when it has not reported or had nothing to say.
    string? ExecutionDetail = null,
    // What a reviewer chose and why, or null for a pending row or a Standing Order — no
    // person chose anything in the latter case, so there is an attestation and no decision record.
    DecisionRecord? Decision = null,
    // Who agreed, or null while nobody has. A Standing Order's attestation is written in the
    // same operation as the filing, so there is no window in which an approved row has no
    // attribution.
    Attestation? Attestation = null,
    // Why this entry cannot be decided, or null when it can. A blocked entry sits in
    // Pending and refuses every decision.
    BlockedMarker? Blocked = null,
    // The composite approval this entry is one constituent of, or null. Until multi-party
    // semantics land, a host composes multi-party approval above the gate: one entry per
    // approver, all naming the same composite, each card stating on its face that it is one of N,
    // and no constituent's approval alone reaching the executor.
    string? CompositeRef = null,
    // The state a reviewer's accepted amendments produced, or null while none has
    // been accepted. Written beside Envelope, which is never edited — a row that
    // overwrote its proposal could not show what the agent originally said, which is the fact an
    // auditor is reading the row for.
    Affidavit? AmendedAffidavit = null,
    // The amendments a decision carried after the deadline had passed, with the act that
    // carried them, or null. An appended later fact on an expired row, written by
    // PreserveAmendmentsAsync and read by a resubmission to
    // prefill the new proposal. Distinct from Amendments, which is what an approval
    // accepted: nobody accepted these, and conflating the two would let a resubmission
    // present a refused caller's corrections as an approval's.
    PreservedAmendments? PreservedAmendments = null,
    // The entry this one resubmits, or null for a first filing. The other half of
    // Lineage; the successor link lives on the superseded row as
    // ResubmittedTo.
    Guid? Supersedes = null,

    // When the row left Pending, or null while it has not.
    DateTimeOffset? DecidedAt = null,

    // The protocol tag this row's shapes conform to. Defaults to Version.
    string ProtocolVersion = AffiantProtocol.Version)
{
    private readonly string? _toolName;

    /// <summary>
    /// The tool or capture source the proposal came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the row because two later questions need it and neither can be answered from the
    /// Affidavit: a resubmission re-runs the coverage lookup against the original tool, and an audit
    /// of a filed write has to be able to say which tool proposed it.
    /// </para>
    /// <para>
    /// <see cref="OperationType"/> is the same fact under the framework's older name and is what
    /// this falls back to when nothing set it explicitly, so a row filed by any release carries a
    /// correct tool name. New code writes and reads <see cref="ToolName"/>.
    /// </para>
    /// </remarks>
    public string ToolName
    {
        get => _toolName ?? OperationType;
        init => _toolName = value;
    }

    /// <summary>
    /// What this entry replaces and what replaced it — <see cref="Supersedes"/> paired with
    /// <see cref="ResubmittedTo"/>, which is the successor link under its older name.
    /// </summary>
    /// <remarks>
    /// A resubmission is a new entry, never a reopened one: the superseded entry keeps its terminal
    /// state and records its successor, so the history reads forward.
    /// </remarks>
    public Lineage Lineage => new(Supersedes, ResubmittedTo);
}
