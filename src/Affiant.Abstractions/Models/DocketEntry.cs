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
    Amended,
    Expired,
    Cancelled,
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
    IReadOnlyDictionary<string, object?>? Amendments);
