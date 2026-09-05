namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// What a Docket store operation is allowed to see.
/// </summary>
/// <remarks>
/// <para>
/// A lookup with the wrong tenant is not an error — it is a miss, indistinguishable from an id that
/// does not exist. Anything else leaks the existence of another tenant's rows to whoever can guess
/// an id. <see cref="ConversationId"/> narrows further, which is what a session surface asks for
/// when it rehydrates one conversation rather than a whole tenant's Docket; it is matched against
/// <see cref="DocketEntry.SessionId"/>.
/// </para>
/// <para>
/// <b>A <c>null</c> <see cref="TenantId"/> is the store-wide scope</b> (<see cref="EntireStore"/>),
/// and it exists for exactly three operators the host itself schedules and no one else: the expiry
/// sweep, retention, and export. Every member of <see cref="IDocketStore"/> that can <em>move</em> a
/// row on a caller's behalf — the guarded transition, the preserved late amendment, the execution
/// report, the supersession — rejects it with an <see cref="ArgumentException"/>, so there is no code
/// path by which a decision reaches a row without naming the tenant it belongs to. That refusal is
/// what makes the tenant check structural rather than a convention each host re-implements.
/// </para>
/// </remarks>
/// <param name="TenantId">The tenant, or <c>null</c> for the host's own store-wide maintenance scope.</param>
/// <param name="ConversationId">One conversation within the tenant, when the caller wants only that.</param>
public sealed record DocketScope(string? TenantId, string? ConversationId = null)
{
    /// <summary>
    /// The whole store — every tenant. Legal only for the host's scheduled sweep, retention and
    /// export; every decision-carrying member refuses it. See this type's remarks.
    /// </summary>
    public static DocketScope EntireStore { get; } = new((string?)null);

    /// <summary>Everything in one tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static DocketScope Tenant(string tenantId) => new(tenantId);

    /// <summary>One conversation within one tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation, matched against <see cref="DocketEntry.SessionId"/>.</param>
    public static DocketScope Conversation(string tenantId, string conversationId) =>
        new(tenantId, conversationId);
}

/// <summary>Where to continue a paged listing, and how much to take.</summary>
/// <remarks>
/// <see cref="Cursor"/> is <b>opaque</b>: produced by a store, understood only by the same store,
/// and bound to the listing that produced it. Feeding one listing's cursor to another is a caller
/// error, not a silently different answer.
/// </remarks>
/// <param name="Limit">How many entries to return at most. Must be greater than zero.</param>
/// <param name="Cursor">The cursor from the previous page, or <c>null</c> to start.</param>
public sealed record DocketPage(int Limit, string? Cursor = null);

/// <summary>One page of a Docket listing.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The entries in this page, in filing order.</param>
/// <param name="Cursor">Pass back as <see cref="DocketPage.Cursor"/> to continue, or <c>null</c> when the listing is drained.</param>
/// <param name="More">Whether another page exists. <c>false</c> when <paramref name="Cursor"/> is <c>null</c>.</param>
public sealed record DocketPageResult<T>(IReadOnlyList<T> Items, string? Cursor, bool More);

/// <summary>
/// What a host's retention job is allowed to remove.
/// </summary>
/// <remarks>
/// Retention is a hook rather than a policy the framework holds an opinion about: how long an
/// approval record must be kept is a legal question with a different answer in every jurisdiction the
/// gate runs in, and a default would be wrong somewhere important. One clause is <em>not</em> the
/// host's to set — retention never ages out an approved row whose write has not been reported,
/// however old, because it is the only record that a write was authorised and has not happened.
/// </remarks>
/// <param name="OlderThan">Remove terminal entries whose terminal instant is strictly before this.</param>
public sealed record DocketRetentionPolicy(DateTimeOffset OlderThan);

/// <summary>
/// The later facts a transition out of <see cref="ReviewStatus.Pending"/> writes.
/// </summary>
/// <remarks>
/// <para>
/// The patch is exactly the set of facts a decision produces and nothing else: it cannot touch the
/// requirement, the deadline, the filing instant or the entry's own id. A row accumulates facts; it
/// is never rewritten.
/// </para>
/// <para>
/// <see cref="AmendedAffidavit"/> is the one apparent exception and it is not one: it is written
/// <em>beside</em> the proposal, never over it. The row keeps <see cref="DocketEntry.Envelope"/> as
/// the agent proposed it and gains the state the approval accepted.
/// </para>
/// </remarks>
/// <param name="Status">The state the entry moves to. Never back to <see cref="ReviewStatus.Pending"/>.</param>
/// <param name="Execution">
/// The execution outcome. Must be <see cref="ExecutionOutcome.Unexecuted"/> when
/// <paramref name="Status"/> is <see cref="ReviewStatus.Approved"/> and <c>null</c> otherwise; a
/// store refuses a patch that contradicts its own status. Left <c>null</c> the store fills in the
/// correct value for the status.
/// </param>
/// <param name="Decision">What the reviewer chose and why. <c>null</c> for a sweep, which nobody decided.</param>
/// <param name="Amendments">The amendments the approval accepted. A <c>null</c> value inside the map clears the field; an absent key leaves it untouched.</param>
/// <param name="AmendedAffidavit">The accepted state those amendments produced.</param>
/// <param name="Attestation">Who agreed. A decision without one is a decision nobody can be held to.</param>
/// <param name="DecidedAt">When the row left pending. Defaults to the store's clock reading.</param>
/// <param name="ExecutionDetail">What the executor reported, when the caller already knows.</param>
/// <param name="SupersededBy">The successor link, for a caller closing and superseding a row in one write.</param>
public sealed record DocketTransitionPatch(
    ReviewStatus Status,
    ExecutionOutcome? Execution = null,
    DecisionRecord? Decision = null,
    IReadOnlyDictionary<string, object?>? Amendments = null,
    Affidavit? AmendedAffidavit = null,
    Attestation? Attestation = null,
    DateTimeOffset? DecidedAt = null,
    string? ExecutionDetail = null,
    Guid? SupersededBy = null);

/// <summary>
/// What a guarded compare-and-set on a Docket row returns.
/// </summary>
/// <remarks>
/// The refusals are distinct because they mean different things to a caller and map to different
/// refusal codes, and each is named after the state it describes.
/// <see cref="AlreadyDecided"/> means <em>someone else decided this entry</em> — the row is approved
/// or rejected and a second decision arrived. <see cref="Expired"/> means <em>the row passed its
/// deadline</em>, whether or not a sweep has recorded that yet, and is the one that must also
/// preserve the amendments the late decision carried
/// (<see cref="IDocketStore.PreserveAmendmentsAsync"/>). <see cref="NotFound"/> is the third answer:
/// no entry with that id is visible in the scope — which, for a caller in the wrong tenant, is the
/// only answer they get.
/// <para>
/// The names are part of the contract, not an implementation detail: a name that describes the
/// <em>other</em> arm's state — "not pending", which is true of both — is one a second implementer
/// will get backwards.
/// </para>
/// </remarks>
public abstract record DocketTransitionResult
{
    private protected DocketTransitionResult() { }

    /// <summary>The compare-and-set applied. <paramref name="Entry"/> is the row as it now stands.</summary>
    /// <param name="Entry">The transitioned row.</param>
    public sealed record Transitioned(DocketEntry Entry) : DocketTransitionResult;

    /// <summary>No entry with that id is visible in the scope.</summary>
    public sealed record NotFound : DocketTransitionResult;

    /// <summary>The row is already approved or rejected: a second decision, refused rather than applied.</summary>
    public sealed record AlreadyDecided : DocketTransitionResult;

    /// <summary>The row passed its deadline, swept or not.</summary>
    public sealed record Expired : DocketTransitionResult;
}

/// <summary>
/// The act a refused late decision was made by: its own instant and its own principal.
/// </summary>
/// <param name="At">When the refused decision was made — not the store's clock, not the row's deadline.</param>
/// <param name="By">Who made it, as the host identifies them.</param>
public sealed record PreservedAct(DateTimeOffset At, string By);

/// <summary>What <see cref="IDocketStore.PreserveAmendmentsAsync"/> returns.</summary>
public abstract record PreserveAmendmentsResult
{
    private protected PreserveAmendmentsResult() { }

    /// <summary>The amendments were appended to the expired row.</summary>
    /// <param name="Entry">The row as it now stands.</param>
    public sealed record Preserved(DocketEntry Entry) : PreserveAmendmentsResult;

    /// <summary>No entry with that id is visible in the scope.</summary>
    public sealed record NotFound : PreserveAmendmentsResult;

    /// <summary>
    /// The entry does not read expired. A caller that gets this has a bug: a decision that was not
    /// refused as expired has no amendments to preserve.
    /// </summary>
    public sealed record NotExpired : PreserveAmendmentsResult;
}

/// <summary>What <see cref="IDocketStore.RecordExecutionAsync"/> returns.</summary>
/// <remarks>
/// <see cref="ExecutionAlreadyRecorded"/> is the execution transition's half of the guarded
/// compare-and-set: the row already carries an outcome, so this report is refused rather than
/// written over the first one. It is a different answer from <see cref="NotApproved"/> — that row
/// was never approved and has no authorised write behind it at all, while this one is approved and
/// already has its outcome on the record.
/// </remarks>
public abstract record RecordExecutionResult
{
    private protected RecordExecutionResult() { }

    /// <summary>The outcome was recorded.</summary>
    /// <param name="Entry">The row as it now stands.</param>
    public sealed record Recorded(DocketEntry Entry) : RecordExecutionResult;

    /// <summary>No entry with that id is visible in the scope.</summary>
    public sealed record NotFound : RecordExecutionResult;

    /// <summary>The row is not approved, so there is no authorised write to report on.</summary>
    public sealed record NotApproved : RecordExecutionResult;

    /// <summary>The row's execution outcome has already moved off <see cref="ExecutionOutcome.Unexecuted"/>.</summary>
    public sealed record ExecutionAlreadyRecorded : RecordExecutionResult;
}

/// <summary>What <see cref="IDocketStore.RecordSupersessionAsync"/> returns.</summary>
public abstract record RecordSupersessionResult
{
    private protected RecordSupersessionResult() { }

    /// <summary>The successor link was recorded.</summary>
    /// <param name="Entry">The row as it now stands.</param>
    public sealed record Recorded(DocketEntry Entry) : RecordSupersessionResult;

    /// <summary>No entry with that id is visible in the scope.</summary>
    public sealed record NotFound : RecordSupersessionResult;

    /// <summary>The row still reads pending, or already names a successor.</summary>
    public sealed record NotTerminal : RecordSupersessionResult;
}

/// <summary>What one bounded expiry sweep call did.</summary>
/// <param name="Expired">The entries this call transitioned to <see cref="ReviewStatus.Expired"/>, in deadline order.</param>
/// <param name="More">Whether another call with the same arguments would find more due entries.</param>
public sealed record ExpireDueResult(IReadOnlyList<DocketEntry> Expired, bool More);

/// <summary>What one bounded retention pass removed.</summary>
/// <param name="Removed">How many rows this call deleted.</param>
/// <param name="More">Whether another call with the same arguments would remove more.</param>
public sealed record RetentionResult(int Removed, bool More);
