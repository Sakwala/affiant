namespace Affiant.Abstractions.Interfaces;

using System.Buffers.Text;
using System.Globalization;
using System.Text;

/// <summary>
/// The opaque cursor the framework's own Docket stores hand out, and the codec a custom store may
/// reuse rather than inventing one.
/// </summary>
/// <remarks>
/// <para>
/// A cursor names the last row a page returned — its ordering instant and its id — so the next page
/// resumes strictly after it. Ordering by an instant alone is not enough: two rows filed in the same
/// tick would make the boundary ambiguous and a page could repeat or skip one, so the id is the
/// tie-break and it is part of the cursor.
/// </para>
/// <para>
/// It is <b>opaque to callers</b> by contract, not by encryption: the encoding is deliberately
/// simple and readable, and a caller that parses one anyway has taken a dependency the store is free
/// to break. What the contract does promise is that a cursor is understood only by the store that
/// produced it and is bound to the listing that produced it — feeding one listing's cursor to
/// another is a caller error, and a store that cannot read a cursor refuses it rather than silently
/// starting from the beginning.
/// </para>
/// </remarks>
public static class DocketCursor
{
    /// <summary>Encodes a page boundary.</summary>
    /// <param name="listing">The listing this cursor belongs to — a store refuses it on any other.</param>
    /// <param name="at">The ordering instant of the last row in the page.</param>
    /// <param name="entryId">That row's id, the tie-break within one instant.</param>
    public static string Encode(string listing, DateTimeOffset at, Guid entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listing);
        var raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{listing}|{at.UtcTicks}|{entryId:N}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>Decodes a page boundary this codec produced for <paramref name="listing"/>.</summary>
    /// <param name="cursor">The cursor a caller passed back, or <c>null</c> to start at the beginning.</param>
    /// <param name="listing">The listing the cursor must belong to.</param>
    /// <param name="at">The ordering instant of the last row of the previous page.</param>
    /// <param name="entryId">That row's id.</param>
    /// <returns>
    /// <c>false</c> when <paramref name="cursor"/> is <c>null</c> or empty — the caller is starting
    /// the listing, which is not an error.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The cursor is unreadable, or belongs to a different listing. Refusing beats silently
    /// restarting: a caller paging with the wrong cursor would otherwise re-read the first page
    /// forever without ever being told.
    /// </exception>
    public static bool TryDecode(
        string? cursor, string listing, out DateTimeOffset at, out Guid entryId)
    {
        at = default;
        entryId = default;
        if (string.IsNullOrEmpty(cursor)) return false;

        Span<byte> buffer = stackalloc byte[256];
        string raw;
        try
        {
            raw = cursor.Length <= 340 && Convert.TryFromBase64String(cursor, buffer, out var written)
                ? Encoding.UTF8.GetString(buffer[..written])
                : Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Docket cursor is not readable.", nameof(cursor), ex);
        }

        var parts = raw.Split('|');
        if (parts.Length != 3
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || !Guid.TryParseExact(parts[2], "N", out entryId))
        {
            throw new ArgumentException("The Docket cursor is not readable.", nameof(cursor));
        }

        if (!string.Equals(parts[0], listing, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"This Docket cursor belongs to the '{parts[0]}' listing, not '{listing}'.",
                nameof(cursor));
        }

        at = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    /// <summary>The listing a cursor was produced for, without decoding the rest of it.</summary>
    /// <param name="cursor">A cursor this codec produced.</param>
    /// <remarks>
    /// For a caller that pages through more than one listing behind a single cursor — a session
    /// rehydration walks pending entries and then approved-unexecuted ones — and so has to know which
    /// listing it is resuming before it can ask for the right one.
    /// </remarks>
    /// <exception cref="ArgumentException">The cursor is unreadable.</exception>
    public static string ListingOf(string cursor)
    {
        ArgumentException.ThrowIfNullOrEmpty(cursor);
        string raw;
        try
        {
            raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Docket cursor is not readable.", nameof(cursor), ex);
        }

        var separator = raw.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0)
            throw new ArgumentException("The Docket cursor is not readable.", nameof(cursor));
        return raw[..separator];
    }

    /// <summary>The listing name <see cref="IDocketStore.ListPendingAsync"/> binds its cursors to.</summary>
    public const string PendingListing = "pending";

    /// <summary>The listing name <see cref="IDocketStore.ListApprovedUnexecutedAsync"/> binds its cursors to.</summary>
    public const string ApprovedUnexecutedListing = "approved-unexecuted";

    /// <summary>
    /// The listing name a session rehydration binds its cursors to while it is still walking pending
    /// entries.
    /// </summary>
    public const string RehydrationPendingListing = "rehydrate-pending";

    /// <summary>
    /// The listing name a session rehydration binds its cursors to once it has moved on to approved
    /// entries whose write has not been reported.
    /// </summary>
    public const string RehydrationApprovedUnexecutedListing = "rehydrate-approved-unexecuted";
}
