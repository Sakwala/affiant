namespace Affiant.Abstractions.Models;

/// <summary>
/// Canonical <see cref="ReviewStatus"/> → <see cref="ReviewOutcome"/> mapping. This is the single
/// source of truth for reporting a <see cref="DocketEntry"/>'s current status as a
/// <see cref="ReviewOutcome"/> — consume it instead of hand-copying the mapping. It was previously
/// reimplemented privately by <c>ReviewGate</c> and, independently, by every host, and two of those
/// three copies had already diverged from each other before this type existed.
/// </summary>
public static class ReviewStatusExtensions
{
    /// <summary>
    /// Maps <paramref name="status"/> to the <see cref="ReviewOutcome"/> a caller should report for
    /// the <see cref="DocketEntry"/> identified by <paramref name="docketId"/>.
    /// <para>
    /// Total over every <see cref="ReviewStatus"/> member, including the two non-terminal ones,
    /// because callers reach this mapping after losing an optimistic-concurrency race or replaying a
    /// decision — the entry's current status is whatever it genuinely is at that moment, terminal or
    /// not:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="ReviewStatus.Pending"/> maps to
    /// <see cref="ReviewOutcome.Expired"/>. A caller only consults this mapping after it can no
    /// longer make forward progress on the entry itself (no live waiter to deliver to, a lost CAS,
    /// a replayed decision); reporting an entry still <c>Pending</c> as anything other than expired
    /// from this call's point of view would overstate what this call can still promise the reviewer
    /// or requester.</description></item>
    /// <item><description><see cref="ReviewStatus.Deferred"/> maps to
    /// <see cref="ReviewOutcome.Referral"/> with escalation path <c>"deferred"</c> — the entry was
    /// handed to a different reviewer via the <c>ReferralRequired</c> approval-policy outcome, which
    /// is itself non-terminal until that reviewer decides.</description></item>
    /// </list>
    /// <para>
    /// Exhaustive by construction, deliberately with no default/discard arm: adding a
    /// <see cref="ReviewStatus"/> member without adding a corresponding arm here fails the build
    /// (<c>CS8509</c>, "the switch expression does not handle all values of its input type" for the
    /// new NAMED member — promoted to an error by <c>TreatWarningsAsErrors</c>) instead of silently
    /// falling through.
    /// </para>
    /// </summary>
    /// <param name="status">The <see cref="DocketEntry.Status"/> to map.</param>
    /// <param name="docketId">The <see cref="DocketEntry.EntryId"/> to carry onto the returned outcome.</param>
#pragma warning disable CS8524 // exhaustive over every NAMED ReviewStatus member (CS8509 stays live); enums admit any underlying integral value via casting (e.g. (ReviewStatus)99), so no finite set of named arms can ever satisfy this diagnostic — see class remarks.
    public static ReviewOutcome ToReviewOutcome(this ReviewStatus status, Guid docketId) =>
        status switch
        {
            ReviewStatus.Pending => new ReviewOutcome.Expired(docketId),
            ReviewStatus.Approved => new ReviewOutcome.Approved(docketId),
            ReviewStatus.Rejected => new ReviewOutcome.Rejected(docketId),
            ReviewStatus.Expired => new ReviewOutcome.Expired(docketId),
            ReviewStatus.Deferred => new ReviewOutcome.Referral(docketId, "deferred")
        };
#pragma warning restore CS8524
}
