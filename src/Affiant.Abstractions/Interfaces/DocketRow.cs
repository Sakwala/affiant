namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// The row semantics every <see cref="IDocketStore"/> shares, in one place: what a row <em>reads</em>
/// as, what a transition patch is allowed to say, what a patch produces, which rows a scope admits,
/// and which instant retention measures a row's age from.
/// </summary>
/// <remarks>
/// These are not conveniences. Each is a rule that would otherwise be restated — and eventually
/// restated differently — in the in-memory store, in each SQL store and in every custom store a host
/// writes. A shared implementation is how the three shipped backends stay each other's reference
/// rather than three near-agreeing dialects.
/// </remarks>
public static class DocketRow
{
    /// <summary>
    /// What <paramref name="entry"/> reads as at <paramref name="now"/> — the status every query path
    /// reports.
    /// </summary>
    /// <param name="entry">The row.</param>
    /// <param name="now">The instant to compare the deadline against.</param>
    /// <remarks>
    /// A pending entry past its deadline reads <see cref="ReviewStatus.Expired"/> <b>whether or not
    /// any sweep has run</b>, and the boundary is inclusive: at the deadline the entry is expired.
    /// Expiry is state, not an event — there is no background job to be down, no alarm to be dropped
    /// and no window in which an entry is decidable because nobody swept it yet. Any other status is
    /// returned unchanged: expiry only ever consumes pending, and a row approved before its deadline
    /// stays approved forever after it.
    /// </remarks>
    public static ReviewStatus ReadStatus(DocketEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Status == ReviewStatus.Pending && entry.ExpiresAt <= now
            ? ReviewStatus.Expired
            : entry.Status;
    }

    /// <summary>The row with expiry applied, ready to hand to a caller.</summary>
    /// <param name="entry">The row as stored.</param>
    /// <param name="now">The instant to compare the deadline against.</param>
    public static DocketEntry Project(DocketEntry entry, DateTimeOffset now)
    {
        var read = ReadStatus(entry, now);
        return read == entry.Status ? entry : entry with { Status = read };
    }

    /// <summary>Whether <paramref name="scope"/> admits <paramref name="entry"/>.</summary>
    /// <param name="entry">The row.</param>
    /// <param name="scope">What the caller may see.</param>
    public static bool InScope(DocketEntry entry, DocketScope scope)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(scope);
        return (scope.TenantId is null || entry.TenantId == scope.TenantId)
            && (scope.ConversationId is null || entry.SessionId == scope.ConversationId);
    }

    /// <summary>
    /// Refuses the store-wide scope on a member that moves a row on a caller's behalf.
    /// </summary>
    /// <param name="scope">The scope the caller passed.</param>
    /// <param name="parameterName">The parameter to name in the exception.</param>
    /// <remarks>
    /// The store-wide scope exists for the host's own sweep, retention and export. A decision, a
    /// preserved late amendment, an execution report and a supersession each name the tenant they
    /// belong to, so that no code path can move a row without one — which is what makes the tenant
    /// check structural rather than a convention each host re-implements.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="scope"/> names no tenant.</exception>
    public static void RequireTenant(DocketScope scope, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.TenantId is null)
        {
            throw new ArgumentException(
                "This operation moves a Docket row and must name the tenant it belongs to; the " +
                "store-wide scope is for the host's own sweep, retention and export only.",
                parameterName);
        }
    }

    /// <summary>
    /// Refuses a row filed in any state but <see cref="ReviewStatus.Pending"/> (AZ-1, DK-1).
    /// </summary>
    /// <remarks>
    /// A Docket row is filed pending and leaves that state only through
    /// <see cref="IDocketStore.TransitionAsync"/>, which is where the attestation is checked. A
    /// store that accepted a row already approved would let a caller file the decided state
    /// directly, with nobody on it, and the guard on the transition would never be consulted — the
    /// row would simply appear, and be returned by the approved-unexecuted listing a host's
    /// executor drains.
    /// </remarks>
    /// <param name="entry">The row being filed.</param>
    /// <param name="paramName">The parameter to name in the exception.</param>
    /// <exception cref="ArgumentException"><paramref name="entry"/> is not pending.</exception>
    public static void ValidateFiling(DocketEntry entry, string paramName)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Status == ReviewStatus.Pending) return;

        throw new ArgumentException(
            $"AZ-1: a Docket entry is filed pending and leaves that state only through a guarded " +
            $"transition, which is where who agreed is checked. This one arrives {entry.Status}. " +
            "Filing a decided row directly would put a state nobody agreed to in front of the "
            + "host's executor.",
            paramName);
    }

    /// <summary>
    /// Refuses an execution report against a row that carries no attestation (AZ-5).
    /// </summary>
    /// <remarks>
    /// The gate refuses it first, and says why. This is the same refusal one layer down, so a
    /// caller holding an <see cref="IDocketStore"/> cannot drive an unattributed row to
    /// <see cref="ExecutionOutcome.Executed"/> either: an executor is reachable only through a
    /// Docket entry that carries an attestation, and that is a property of the row rather than of
    /// the path that reached it.
    /// </remarks>
    /// <param name="entry">The row the outcome is being reported against.</param>
    /// <returns><see langword="true"/> when the row may be executed against.</returns>
    public static bool MayRecordExecution(DocketEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Attestation is not null;
    }

    /// <summary>Refuses a transition patch that contradicts itself.</summary>
    /// <param name="patch">The patch.</param>
    /// <param name="expected">The state the caller says the row is in.</param>
    /// <param name="patchParameterName">The patch parameter to name in the exception.</param>
    /// <param name="expectedParameterName">The expected-state parameter to name in the exception.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="expected"/> is not <see cref="ReviewStatus.Pending"/>, the patch moves the row
    /// back to pending, or its execution outcome contradicts its status.
    /// </exception>
    public static void ValidateTransition(
        DocketTransitionPatch patch,
        ReviewStatus expected,
        string patchParameterName,
        string expectedParameterName)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (expected != ReviewStatus.Pending)
        {
            throw new ArgumentException(
                "Pending is the only state with a transition out of it.", expectedParameterName);
        }

        if (patch.Status == ReviewStatus.Pending)
        {
            throw new ArgumentException(
                "A transition leaves pending; it never returns to it.", patchParameterName);
        }

        if (patch.Status == ReviewStatus.Approved && patch.Execution is not null
            && patch.Execution != ExecutionOutcome.Unexecuted)
        {
            throw new ArgumentException(
                "An approval files the write unexecuted; the outcome is the host's to report once, " +
                "through RecordExecutionAsync.",
                patchParameterName);
        }

        if (patch.Status != ReviewStatus.Approved && patch.Execution is not null)
        {
            throw new ArgumentException(
                $"A {patch.Status} entry carries no execution outcome.", patchParameterName);
        }

        // AZ-1: a row that leaves pending approved or rejected names who agreed, or it is not
        // written at all.
        //
        // This is one of the store's three attestation guards, and together they make the state
        // unwritable: a row may only be FILED pending, it may only LEAVE pending through this
        // transition, and an execution outcome may only be recorded against a row that carries an
        // attestation. The gate's decision core makes an unattested approval unreachable; these
        // three make it unstorable, including for a caller holding an IDocketStore directly. A
        // decided row that cannot name who agreed is not a record, and an executor reached through
        // one would be a write nobody authorised (AZ-5). Expiry is unaffected — nobody decided it,
        // so it carries neither.
        if (patch.Status is not (ReviewStatus.Approved or ReviewStatus.Rejected))
            return;

        if (patch.Attestation is null)
        {
            throw new ArgumentException(
                $"AZ-1: a transition to {patch.Status} carries an attestation. A decided row that " +
                "cannot name who agreed is not a record, and an executor reached through one would " +
                "be a write nobody authorised (AZ-5).",
                patchParameterName);
        }

        // The decision record is what a PERSON chose and why. A Standing Order approval has an
        // attestation and no decision record, because nobody chose anything — so the requirement is
        // read off the attestor rather than applied to every approval alike.
        if (patch.Attestation.By is not Attestor.StandingOrder && patch.Decision is null)
        {
            throw new ArgumentException(
                $"AZ-1: a transition to {patch.Status} attested to " +
                $"{patch.Attestation.By.Kind} carries a decision record saying what that person " +
                "chose and why. Only a Standing Order approval has none, because nobody chose " +
                "anything.",
                patchParameterName);
        }
    }

    /// <summary>Refuses an execution report that contradicts the guard it claims to run under.</summary>
    /// <param name="outcome">The outcome the host is reporting.</param>
    /// <param name="expected">The execution state the caller says the row is in.</param>
    /// <param name="outcomeParameterName">The outcome parameter to name in the exception.</param>
    /// <param name="expectedParameterName">The expected-state parameter to name in the exception.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="outcome"/> is <see cref="ExecutionOutcome.Unexecuted"/> — that is where a row
    /// starts, not something an executor reports — or <paramref name="expected"/> is anything else,
    /// because the execution transition runs once, out of the state a row is approved in.
    /// </exception>
    public static void ValidateExecutionReport(
        ExecutionOutcome outcome,
        ExecutionOutcome expected,
        string outcomeParameterName,
        string expectedParameterName)
    {
        if (outcome == ExecutionOutcome.Unexecuted)
        {
            throw new ArgumentException(
                "Unexecuted is where an approved row starts; an executor reports Executed or Failed.",
                outcomeParameterName);
        }

        if (expected != ExecutionOutcome.Unexecuted)
        {
            throw new ArgumentException(
                "The execution outcome is recorded once, out of Unexecuted.", expectedParameterName);
        }
    }

    /// <summary>The row a transition patch produces from <paramref name="entry"/>.</summary>
    /// <param name="entry">The row as stored, still pending.</param>
    /// <param name="patch">The later facts the transition writes.</param>
    /// <param name="now">The store's clock reading, used when the patch names no decision instant.</param>
    /// <remarks>
    /// Every field the patch leaves unset keeps what the row already held: a transition appends
    /// facts, it never blanks them. An approved row is given
    /// <see cref="ExecutionOutcome.Unexecuted"/> and every other terminal row <c>null</c>, because
    /// the execution outcome is non-null exactly when the status is approved.
    /// </remarks>
    public static DocketEntry Apply(DocketEntry entry, DocketTransitionPatch patch, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(patch);

        return entry with
        {
            Status = patch.Status,
            Execution = patch.Status == ReviewStatus.Approved
                ? patch.Execution ?? ExecutionOutcome.Unexecuted
                : null,
            ExecutionDetail = patch.ExecutionDetail ?? entry.ExecutionDetail,
            Decision = patch.Decision ?? entry.Decision,
            Attestation = patch.Attestation ?? entry.Attestation,
            Amendments = patch.Amendments ?? entry.Amendments,
            AmendedAffidavit = patch.AmendedAffidavit ?? entry.AmendedAffidavit,
            DecidedAt = patch.DecidedAt ?? entry.DecidedAt ?? now,
            ResubmittedTo = patch.SupersededBy ?? entry.ResubmittedTo
        };
    }

    /// <summary>
    /// The instant retention measures a terminal row's age from, or <c>null</c> for a row that still
    /// reads pending.
    /// </summary>
    /// <param name="entry">The row.</param>
    /// <param name="now">The instant to compare the deadline against.</param>
    /// <remarks>
    /// A decided row is measured from when it left pending. A row that lapsed without a decision has
    /// no decision instant, so it is measured from its deadline — the moment it became terminal.
    /// </remarks>
    public static DateTimeOffset? TerminalAt(DocketEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var read = ReadStatus(entry, now);
        if (read == ReviewStatus.Pending) return null;
        return entry.DecidedAt ?? (read == ReviewStatus.Expired ? entry.ExpiresAt : entry.CreatedAt);
    }

    /// <summary>
    /// Whether retention must keep <paramref name="entry"/> however old it is.
    /// </summary>
    /// <param name="entry">The row.</param>
    /// <remarks>
    /// An approved row whose write has not been reported is the only record that a write was
    /// authorised and has not happened. Ageing it out destroys the evidence that the outstanding work
    /// exists, so no retention policy may remove it.
    /// </remarks>
    public static bool IsApprovedUnexecuted(DocketEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Status == ReviewStatus.Approved
            && entry.Execution is null or ExecutionOutcome.Unexecuted;
    }
}
