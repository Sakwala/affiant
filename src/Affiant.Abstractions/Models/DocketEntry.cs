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
    string? ReviewerUserId,
    string OperationType,
    Affidavit Envelope,
    ReviewStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, object?>? Amendments,
    Guid? ResubmittedTo = null);
