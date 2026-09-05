namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// The order a reconnecting client is given its Docket back: everything that still reads
/// <see cref="ReviewStatus.Pending"/>, then everything <see cref="ReviewStatus.Approved"/> whose
/// write has not been reported — each in filing order, and paged.
/// </summary>
/// <remarks>
/// <para>
/// The order is a rule rather than a preference because the two groups ask different things of the
/// person reconnecting: the first still needs a decision, the second still needs execution. A client
/// that showed them interleaved would put work that is already agreed in front of work that is still
/// blocked on the reader, and the reader would work the wrong queue first.
/// </para>
/// <para>
/// An entry that reads expired is never rehydrated as pending, swept or not — the pending listing
/// applies the deadline, so a lapsed entry is simply absent rather than present-and-undecidable.
/// </para>
/// <para>
/// One cursor carries the whole sequence, including which of the two groups it is resuming, so a page
/// boundary that falls between them resumes at the start of the second rather than restarting the
/// first. A page whose limit is not spent by the pending group <b>fills from the second group in the
/// same page</b>: a page that stopped at the boundary would report more with a limit it had not
/// spent. The cursor is opaque; a caller passes back what the previous page returned and stops when
/// it is <c>null</c>.
/// </para>
/// </remarks>
public static class DocketRehydration
{
    /// <summary>
    /// One page of the rehydration sequence.
    /// </summary>
    /// <param name="store">The Docket.</param>
    /// <param name="scope">The conversation being rehydrated, and the tenant it belongs to.</param>
    /// <param name="page">Where to continue and how much to take.</param>
    /// <param name="ct">Caller cancellation.</param>
    public static async Task<DocketPageResult<DocketEntry>> PageAsync(
        IDocketStore store, DocketScope scope, DocketPage page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(page);

        var resumingApproved = page.Cursor is not null
            && DocketCursor.ListingOf(page.Cursor) == DocketCursor.RehydrationApprovedUnexecutedListing;

        if (!resumingApproved)
        {
            var pending = await store.ListPendingAsync(
                scope,
                new DocketPage(page.Limit, TranslateCursor(
                    page.Cursor, DocketCursor.RehydrationPendingListing, DocketCursor.PendingListing)),
                ct);

            if (pending.More && pending.Cursor is not null)
            {
                return pending with
                {
                    Cursor = Retag(pending.Cursor, DocketCursor.RehydrationPendingListing)
                };
            }

            // The pending group is drained and the page still has room, so it fills from the second
            // group in the same page (DK-5). A page that stopped at the group boundary would report
            // `more` with a limit it had not spent, and a caller asking for ten rows over a Docket
            // holding two would be told there were more.
            var remaining = page.Limit - pending.Items.Count;
            if (remaining <= 0)
            {
                var probe = await store.ListApprovedUnexecutedAsync(scope, new DocketPage(1), ct);
                return probe.Items.Count == 0
                    ? new DocketPageResult<DocketEntry>(pending.Items, null, false)
                    : new DocketPageResult<DocketEntry>(pending.Items, ApprovedGroupStart, true);
            }

            var spill = await store.ListApprovedUnexecutedAsync(scope, new DocketPage(remaining), ct);
            var filled = pending.Items.Count == 0
                ? spill.Items
                : [.. pending.Items, .. spill.Items];

            return spill.More && spill.Cursor is not null
                ? new DocketPageResult<DocketEntry>(
                    filled, Retag(spill.Cursor, DocketCursor.RehydrationApprovedUnexecutedListing), true)
                : new DocketPageResult<DocketEntry>(filled, null, false);
        }

        var approved = await store.ListApprovedUnexecutedAsync(
            scope,
            new DocketPage(page.Limit, TranslateCursor(
                page.Cursor,
                DocketCursor.RehydrationApprovedUnexecutedListing,
                DocketCursor.ApprovedUnexecutedListing)),
            ct);

        return approved.More && approved.Cursor is not null
            ? approved with
            {
                Cursor = Retag(approved.Cursor, DocketCursor.RehydrationApprovedUnexecutedListing)
            }
            : new DocketPageResult<DocketEntry>(approved.Items, null, false);
    }

    /// <summary>
    /// The whole sequence, drained page by page.
    /// </summary>
    /// <param name="store">The Docket.</param>
    /// <param name="scope">The conversation being rehydrated, and the tenant it belongs to.</param>
    /// <param name="pageSize">How many entries to read at a time.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// For a caller that genuinely wants everything — a small session, a test — expressed in terms of
    /// the paged primitive rather than beside it, so there is no second definition of the order.
    /// </remarks>
    public static async Task<IReadOnlyList<DocketEntry>> AllAsync(
        IDocketStore store, DocketScope scope, int pageSize, CancellationToken ct)
    {
        var all = new List<DocketEntry>();
        string? cursor = null;
        do
        {
            var page = await PageAsync(store, scope, new DocketPage(pageSize, cursor), ct);
            all.AddRange(page.Items);
            cursor = page.Cursor;
        }
        while (cursor is not null);
        return all;
    }

    /// <summary>
    /// The cursor meaning "the start of the approved-unexecuted group" — a boundary at the beginning
    /// of time, which every row is after.
    /// </summary>
    private static string ApprovedGroupStart { get; } = DocketCursor.Encode(
        DocketCursor.RehydrationApprovedUnexecutedListing, DateTimeOffset.MinValue, Guid.Empty);

    /// <summary>Re-tags a store's cursor as this sequence's, so a caller sees one listing, not two.</summary>
    private static string Retag(string storeCursor, string listing)
    {
        var sourceListing = DocketCursor.ListingOf(storeCursor);
        DocketCursor.TryDecode(storeCursor, sourceListing, out var at, out var entryId);
        return DocketCursor.Encode(listing, at, entryId);
    }

    /// <summary>Turns this sequence's cursor back into one the underlying listing will accept.</summary>
    private static string? TranslateCursor(string? cursor, string fromListing, string toListing)
    {
        if (cursor is null) return null;
        DocketCursor.TryDecode(cursor, fromListing, out var at, out var entryId);

        // The sentinel that opens a group: there is no boundary to resume from, so the listing starts
        // at its own beginning.
        if (at == DateTimeOffset.MinValue && entryId == Guid.Empty) return null;
        return DocketCursor.Encode(toListing, at, entryId);
    }
}
