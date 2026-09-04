namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// The framework's review ledger: durable storage for pending, approved, rejected and expired
/// <see cref="DocketEntry"/> records, plus the per-session <see cref="ConversationContext"/> the
/// review was raised against. Implement it to back the docket with your own store; ship-ready
/// implementations for in-memory, SQLite and PostgreSQL come with <c>Affiant.Docket</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is a correctness-critical contract, not a CRUD wrapper. Two members carry explicit
/// atomicity obligations that <c>ReviewGate</c> relies on and that a naive read-then-write
/// implementation will violate: <see cref="TransitionAsync"/> (double-submit prevention) and
/// <see cref="ConsumeForResubmitAsync"/> (double-resubmit prevention). Both express their result as
/// an outcome the caller can branch on — this caller won, or somebody else already did — so the
/// guard must live in the write itself, never in surrounding C#. Read each member's remarks before
/// implementing; the per-member contracts are the specification.
/// </para>
/// <para>
/// A store is expected to be safe for concurrent use across sessions and across callers within a
/// session. <c>Affiant.Testing.ComplianceHarness</c> exercises these invariants against any
/// implementation, including the ordering contract on
/// <see cref="ListPendingBySessionAsync"/>.
/// </para>
/// </remarks>
public interface IDocketStore
{
    /// <summary>
    /// Persist the accumulated entity state for a session.
    /// The ConversationContext captures domain-agnostic EntityRef objects; host adapters
    /// are responsible for encoding domain-specific state into the EntityRef dictionary.
    /// </summary>
    Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct);

    /// <summary>Load a session's entity state. Returns null for sessions with no persisted context.</summary>
    Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// File a new DocketEntry into the review queue.
    /// Implementations must enforce an idempotency guard on EntryId:
    /// a second call with the same EntryId is a no-op.
    /// </summary>
    /// <remarks>
    /// A row is filed <see cref="ReviewStatus.Pending"/> and leaves that state only through
    /// <see cref="TransitionAsync"/>, which is where who agreed is checked (AZ-1). A store refuses
    /// a row that arrives in any other state: filing a decided row directly would put a state
    /// nobody agreed to in front of the host's executor, and the transition's guard would never be
    /// consulted.
    /// </remarks>
    Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct);

    /// <summary>
    /// Reads one entry, or <c>null</c> when <paramref name="entryId"/> names no entry.
    /// </summary>
    /// <remarks>
    /// <para><strong>Expiry is a queryable state, not a swept-in one.</strong></para>
    /// <para>
    /// An entry whose persisted status is <see cref="ReviewStatus.Pending"/> but whose
    /// <see cref="DocketEntry.ExpiresAt"/> is on or before the current instant MUST be returned
    /// with <see cref="DocketEntry.Status"/> = <see cref="ReviewStatus.Expired"/>, whether or not
    /// an expiry sweep has run. The boundary is inclusive: at exactly
    /// <see cref="DocketEntry.ExpiresAt"/> the entry is expired.
    /// </para>
    /// <para>
    /// The projection is a read-time one — it does not write. The sweep
    /// (<c>Affiant.Docket.Services.DocketExpiryService</c>) still persists
    /// <see cref="ReviewStatus.Expired"/> onto the row, and the guarded
    /// <see cref="TransitionAsync"/> / <see cref="ConsumeForResubmitAsync"/> writes still test the
    /// <em>persisted</em> status, so a caller that needs the transition durably recorded (a
    /// resubmission, an audit read after a restart) must still let the sweep — or its own
    /// <see cref="ExpireDueAsync"/> call — commit it.
    /// </para>
    /// <para>
    /// Implementations read the current instant from an injected <see cref="TimeProvider"/>, never
    /// from <c>DateTimeOffset.UtcNow</c>, so a test can move the clock.
    /// </para>
    /// </remarks>
    Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct);


    /// <summary>
    /// Atomically claims <paramref name="entryId"/> for resubmission by recording
    /// <paramref name="newEntryId"/> onto its <see cref="DocketEntry.ResubmittedTo"/> — the race
    /// guard and lineage record for <c>ReviewGate.ResubmitAsync</c> (Area-5 Decision 2, affiant#31).
    /// </summary>
    /// <returns>
    /// The number of rows affected (1 if this call won the claim, 0 if <paramref name="entryId"/>
    /// was not found, is not <see cref="ReviewStatus.Expired"/>, or a concurrent caller already
    /// claimed it — see remarks for the double-resubmit contract).
    /// </returns>
    /// <remarks>
    /// <para><strong>Double-Resubmit Prevention Contract:</strong></para>
    /// <para>
    /// Implementations MUST enforce atomic read-before-write semantics with a guard condition
    /// equivalent to <c>WHERE Status = 'Expired' AND ResubmittedTo IS NULL</c> — the same
    /// compare-and-set idiom <see cref="TransitionAsync"/> uses for its own guard — so two
    /// concurrent calls for the same <paramref name="entryId"/> can never both succeed.
    /// </para>
    /// <para>
    /// Unlike <see cref="TransitionAsync"/>, this does not transition <see cref="DocketEntry.Status"/>:
    /// there is no <c>ReviewStatus.Resubmitted</c> by design (Area-5 Decision 2). The entry stays
    /// <see cref="ReviewStatus.Expired"/>; <see cref="DocketEntry.ResubmittedTo"/> alone records that
    /// it was superseded and by which new entry.
    /// </para>
    /// </remarks>
    Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct);

    /// <summary>
    /// Reverse lookup for resubmission lineage: finds the <see cref="DocketEntry"/> whose
    /// <see cref="DocketEntry.ResubmittedTo"/> equals <paramref name="entryId"/> — i.e. the expired
    /// entry that <paramref name="entryId"/> was resubmitted from, if any.
    /// </summary>
    /// <returns>The parent entry, or <c>null</c> if <paramref name="entryId"/> was not itself produced by a resubmission.</returns>
    /// <remarks>
    /// Closes the silent-loss window in <see cref="Transport.EvidenceCardRequest.PriorAmendments"/>:
    /// that field only ever travels on the transient resubmission broadcast, never onto the new
    /// entry's own <see cref="DocketEntry.Amendments"/>. A reconnect that arrives after the broadcast
    /// was already consumed (or missed) uses this lookup to re-derive the same
    /// <see cref="DocketEntry.Amendments"/> from the parent — see
    /// <c>SessionRehydrator.RehydrateAsync</c>.
    /// </remarks>
    Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct);


    /// <summary>
    /// Returns every <see cref="ReviewStatus.Pending"/> entry for <paramref name="sessionId"/>,
    /// ordered by <see cref="DocketEntry.CreatedAt"/> ascending — oldest-filed entry first (Area-5
    /// Decision 3 / P2d rider, affiant#28). Both <c>SessionRehydrator</c> and
    /// <c>ReviewGate.RebroadcastPendingCardsAsync</c> rely on this order to replay a session's
    /// stranded reviews in the sequence they were originally filed.
    /// </summary>
    /// <remarks>
    /// Expiry is a queryable state (see <see cref="GetDocketEntryAsync"/>): an entry whose
    /// <see cref="DocketEntry.ExpiresAt"/> is on or before the current instant is no longer pending
    /// and MUST NOT appear here, swept or not. That is also what keeps a lapsed entry from being
    /// rehydrated as pending on reconnect.
    /// </remarks>
    [Obsolete(
        "Every listing a store exposes is paged with an opaque cursor and scoped to a tenant; this " +
        "one is neither, so a session with more stranded reviews than fit in memory has no bounded " +
        "read. Use ListPendingAsync(DocketScope.Conversation(tenantId, sessionId), page, ct). Kept " +
        "for one release.",
        error: false,
        DiagnosticId = "AFFIANT0001")]
    Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Returns every <see cref="ReviewStatus.Pending"/> entry across all sessions, in no specified
    /// order — the listing primitive <c>DocketExpiryService</c>'s sweep uses to re-broadcast
    /// <see cref="Transport.TransportEvent.EvidenceCardRequest"/> unconditionally each tick,
    /// independent of whether the entry's filing-time broadcast reported success (Area-5 Decision 3,
    /// affiant#28). Unlike <see cref="ListPendingBySessionAsync"/>, this is not scoped to a session
    /// and carries no ordering contract — callers that need a stable order per session should use
    /// that method instead.
    /// </summary>
    /// <remarks>
    /// Expiry is a queryable state (see <see cref="GetDocketEntryAsync"/>): an entry whose
    /// <see cref="DocketEntry.ExpiresAt"/> is on or before the current instant is no longer pending
    /// and MUST NOT appear here, swept or not — so the sweep's own re-broadcast phase never
    /// re-broadcasts a card for an entry that has already run out of time.
    /// </remarks>
    [Obsolete(
        "Every listing a store exposes is paged with an opaque cursor; this one loads every pending " +
        "row in the store. Use ListPendingAsync(DocketScope.EntireStore, page, ct). Kept for one " +
        "release.",
        error: false,
        DiagnosticId = "AFFIANT0001")]
    Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct);

    // ── The scoped, guarded, paged surface ──────────────────────────────────
    // Everything below is the Docket's real contract. The members above it are the framework's
    // earlier, unscoped shapes, kept for one release where they still have a caller. Three
    // properties are worth reading this half of the interface for.
    //
    // EVERY DECISION-CARRYING OPERATION IS SCOPED. There is no member here that moves a row by id
    // alone. An entry id is unique WITHIN a tenant, and a lookup with the wrong tenant is not an
    // error — it is a miss, indistinguishable from an id that does not exist.
    //
    // EVERY READ APPLIES EXPIRY. A pending entry past its deadline reads Expired whether or not the
    // host's sweep has run, and is absent from the pending listings. A store therefore needs to know
    // the time, which is why it is built with a TimeProvider rather than handed an instant per call:
    // a store that took `now` as a parameter would let a caller answer the deadline question for it.
    //
    // EVERY LIST IS BOUNDED. No member returns "all of them". Listings are paged with an opaque
    // cursor; the sweep and retention take a limit and report whether more remain; export streams.

    /// <summary>
    /// Move an entry out of <see cref="ReviewStatus.Pending"/>, if it is still pending — a guarded
    /// compare-and-set.
    /// </summary>
    /// <param name="entryId">The entry to transition.</param>
    /// <param name="scope">The tenant (and optionally the conversation) the caller may see. The store-wide scope is refused.</param>
    /// <param name="expected">
    /// <see cref="ReviewStatus.Pending"/> and only pending — nothing else in the state machine has a
    /// transition out of it. Present so the guard is visible at the call site rather than implied.
    /// </param>
    /// <param name="patch">The later facts the transition writes.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// <para>
    /// The read of the current state and the write of the new one happen with no interleaving point
    /// between them — a real conditional <c>UPDATE</c> on a SQL store, a synchronous block in memory
    /// — so of two decisions that race, exactly one is applied and the other is refused. A second
    /// decision, a decision that lost a race and a decision on a row past its deadline are three
    /// distinct answers (<see cref="DocketTransitionResult.AlreadyDecided"/> and
    /// <see cref="DocketTransitionResult.Expired"/>), never silently applied and never overwritten.
    /// </para>
    /// <para>
    /// A row carrying a <see cref="DocketEntry.Blocked"/> marker refuses every transition with
    /// <see cref="DocketTransitionResult.AlreadyDecided"/>: it sits in pending and is never decided,
    /// never executed and never degraded to a weaker requirement.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="scope"/> is the store-wide scope, or <paramref name="expected"/> is not
    /// <see cref="ReviewStatus.Pending"/>.
    /// </exception>
    Task<DocketTransitionResult> TransitionAsync(
        Guid entryId,
        DocketScope scope,
        ReviewStatus expected,
        DocketTransitionPatch patch,
        CancellationToken ct);

    /// <summary>
    /// Record the amendments a decision carried after the entry had already expired, so a
    /// resubmission can prefill them.
    /// </summary>
    /// <param name="entryId">The expired entry.</param>
    /// <param name="scope">The tenant the caller may see. The store-wide scope is refused.</param>
    /// <param name="amendments">The map the refused decision carried.</param>
    /// <param name="act">The refused decision's own instant and principal.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// <para>
    /// This is the one write that applies to a row the transition guard refused, and it is a separate
    /// member for that reason: folding it into <see cref="TransitionAsync"/> would make a refused
    /// compare-and-set write to the row, and "applied once or not at all" is exactly the property the
    /// guard exists to have. It is an appended later fact on a terminal row, not an edit of a
    /// recorded decision — it touches <see cref="DocketEntry.PreservedAmendments"/> and nothing else,
    /// never the status, the decision or the attestation.
    /// </para>
    /// <para>
    /// <paramref name="act"/> is the refused decision's <b>own</b> instant and principal, not the
    /// store's clock and not the row's deadline: a resubmission prefills these values as a person's
    /// correction and binds each prefilled field's tag to that act, so a record that dated them to
    /// the sweep would place the correction at a moment nobody typed anything.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is the store-wide scope.</exception>
    Task<PreserveAmendmentsResult> PreserveAmendmentsAsync(
        Guid entryId,
        DocketScope scope,
        IReadOnlyDictionary<string, object?> amendments,
        PreservedAct act,
        CancellationToken ct);

    /// <summary>
    /// Record what the host's executor reported for an approved entry, <b>once</b>.
    /// </summary>
    /// <param name="entryId">The approved entry.</param>
    /// <param name="scope">The tenant the caller may see. The store-wide scope is refused.</param>
    /// <param name="outcome"><see cref="ExecutionOutcome.Executed"/> or <see cref="ExecutionOutcome.Failed"/>.</param>
    /// <param name="detail">What the executor reported, or <c>null</c>.</param>
    /// <param name="expected">
    /// <see cref="ExecutionOutcome.Unexecuted"/> and only that: the execution transition runs once,
    /// out of the state a row is approved in.
    /// </param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// <para>
    /// The status stays <see cref="ReviewStatus.Approved"/>; only
    /// <see cref="DocketEntry.Execution"/> and <see cref="DocketEntry.ExecutionDetail"/> move. The
    /// framework never performs the write — it records what the host says happened, so an
    /// approved-but-failed write is distinguishable from an approved-and-committed one on the row.
    /// </para>
    /// <para>
    /// A guarded compare-and-set, exactly like <see cref="TransitionAsync"/>. A second report is
    /// refused with <see cref="RecordExecutionResult.ExecutionAlreadyRecorded"/> rather than written
    /// on top: without the guard an executed row could be flipped to failed by a later caller, an
    /// edit in place of a recorded fact. The consequence for a host is one sentence — <b>a host that
    /// retries a write reports once, when it knows the outcome.</b> The retries are the host's
    /// business; the outcome is the Docket's.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="scope"/> is the store-wide scope, <paramref name="outcome"/> is
    /// <see cref="ExecutionOutcome.Unexecuted"/>, or <paramref name="expected"/> is not
    /// <see cref="ExecutionOutcome.Unexecuted"/>.
    /// </exception>
    Task<RecordExecutionResult> RecordExecutionAsync(
        Guid entryId,
        DocketScope scope,
        ExecutionOutcome outcome,
        string? detail,
        ExecutionOutcome expected,
        CancellationToken ct);

    /// <summary>
    /// Record that a terminal entry has been resubmitted, naming its successor.
    /// </summary>
    /// <param name="entryId">The superseded entry.</param>
    /// <param name="scope">The tenant the caller may see. The store-wide scope is refused.</param>
    /// <param name="supersededBy">The new entry that replaces it.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// The superseded entry keeps its terminal state; only the successor link is added. A
    /// resubmission is a new entry, never a reopened one, which is what lets the history read
    /// forward. The link is written once — a row that already names a successor answers
    /// <see cref="RecordSupersessionResult.NotTerminal"/>, which is also the answer for a row that
    /// still reads pending.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is the store-wide scope.</exception>
    Task<RecordSupersessionResult> RecordSupersessionAsync(
        Guid entryId,
        DocketScope scope,
        Guid supersededBy,
        CancellationToken ct);

    /// <summary>
    /// Record on a pending entry that it cannot be decided, and why.
    /// </summary>
    /// <param name="entryId">The entry.</param>
    /// <param name="scope">What the caller may see. A row outside it is not found, never blocked.</param>
    /// <param name="marker">Why it cannot be decided.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <returns>1 when this call wrote the marker, 0 when the row was not pending or already carries one.</returns>
    /// <remarks>
    /// <para>
    /// A blocked entry stays in <see cref="ReviewStatus.Pending"/>: it is recorded, its card says on
    /// its face that it is blocked, and every decision on it is refused. It is never executed and
    /// never degraded to a weaker requirement — a joint requirement quietly satisfied by one
    /// approval is the failure the marker exists to prevent.
    /// </para>
    /// <para>
    /// Written once, under the same kind of guard every other transition uses: a marker that could
    /// be overwritten could be cleared, and an entry whose blocked marker was cleared is an entry
    /// that became decidable without anyone deciding it should be.
    /// </para>
    /// </remarks>
    /// <para>
    /// Scoped, like every other member that moves a row: a marker written by entry id alone would
    /// let any caller holding an id make another tenant's row permanently undecidable, since the
    /// guard that stops a marker being overwritten also stops it being cleared.
    /// </para>
    Task<int> MarkBlockedAsync(
        Guid entryId, DocketScope scope, BlockedMarker marker, CancellationToken ct);

    /// <summary>
    /// How many entries read <see cref="ReviewStatus.Pending"/> right now, across the whole store.
    /// </summary>
    /// <param name="ct">Caller cancellation.</param>
    /// <returns>The count. Never the rows.</returns>
    /// <remarks>
    /// <para>
    /// For a depth gauge and nothing else: an operator wants to know how much work is waiting, and
    /// a number is the whole answer. DK-3 says a store never loads the whole Docket into memory, so
    /// the one caller that wanted a depth reading must be able to ask for a depth rather than for
    /// every row and a <c>.Count</c> — which is what it used to do, unpaged and across every tenant,
    /// every fifteen seconds.
    /// </para>
    /// <para>
    /// A SQL store answers with <c>COUNT(*)</c>; the in-memory store counts what it holds. Both
    /// apply the deadline, so a row past its expiry is not pending and is not counted, swept or not.
    /// </para>
    /// </remarks>
    Task<long> CountPendingAsync(CancellationToken ct);

    /// <summary>Entries that read <see cref="ReviewStatus.Pending"/> right now, in filing order, paged.</summary>
    /// <param name="scope">What the caller may see.</param>
    /// <param name="page">Where to continue and how much to take.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>An entry past its deadline is not pending and does not appear here, swept or not.</remarks>
    Task<DocketPageResult<DocketEntry>> ListPendingAsync(
        DocketScope scope, DocketPage page, CancellationToken ct);

    /// <summary>
    /// Entries that are <see cref="ReviewStatus.Approved"/> and still
    /// <see cref="ExecutionOutcome.Unexecuted"/>, in filing order, paged.
    /// </summary>
    /// <param name="scope">What the caller may see.</param>
    /// <param name="page">Where to continue and how much to take.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// The host's executor reads this: an approved write nobody has reported on is work outstanding,
    /// and after a restart it is the only record that the work exists.
    /// </remarks>
    Task<DocketPageResult<DocketEntry>> ListApprovedUnexecutedAsync(
        DocketScope scope, DocketPage page, CancellationToken ct);

    /// <summary>
    /// Transition at most <paramref name="limit"/> entries due at <paramref name="now"/> to
    /// <see cref="ReviewStatus.Expired"/>, oldest deadline first, and report whether more remain.
    /// </summary>
    /// <param name="now">The instant to compare deadlines against — inclusive: at the deadline the entry is expired.</param>
    /// <param name="scope">What the sweep may see. <see cref="DocketScope.EntireStore"/> is legal here.</param>
    /// <param name="limit">The page size. Must be greater than zero.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// <para>
    /// The host schedules this; no framework package owns a timer. The sweep makes the state durable
    /// and gives the host a list to notify on — it does not <em>cause</em> expiry, which every read
    /// already applies. <see cref="ExpireDueResult.More"/> says whether another call would find more,
    /// so a host drains the queue in bounded steps rather than in one unbounded pass, and the store
    /// never loads the whole Docket into memory.
    /// </para>
    /// <para>
    /// Only the rows <em>this call's own</em> guarded write transitioned are returned: an entry a
    /// concurrent decision claimed a beat earlier belongs to that caller, and a caller that broadcast
    /// on it here would double-notify.
    /// </para>
    /// </remarks>
    Task<ExpireDueResult> ExpireDueAsync(
        DateTimeOffset now, DocketScope scope, int limit, CancellationToken ct);

    /// <summary>
    /// Remove at most <paramref name="limit"/> terminal entries older than
    /// <see cref="DocketRetentionPolicy.OlderThan"/>, and report whether more remain.
    /// </summary>
    /// <param name="policy">What the host's retention job is allowed to remove.</param>
    /// <param name="scope">What the job may see.</param>
    /// <param name="limit">The page size. Must be greater than zero.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// <b>Retention never ages out an approved row whose write has not been reported</b>, however
    /// old: it is the only record that a write was authorised and has not happened. Every other
    /// terminal row — rejected, expired, approved-and-executed, approved-and-failed — is the host's
    /// to age out. Each call shrinks the eligible set, so a host drains retention by calling until
    /// <see cref="RetentionResult.More"/> is <c>false</c>.
    /// </remarks>
    Task<RetentionResult> ApplyRetentionAsync(
        DocketRetentionPolicy policy, DocketScope scope, int limit, CancellationToken ct);

    /// <summary>Remove everything belonging to <paramref name="tenantId"/>.</summary>
    /// <param name="tenantId">The tenant whose data is being deleted.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <returns>How many rows were removed.</returns>
    /// <remarks>
    /// Unbounded by design and by necessity: a tenant asking for their data to be deleted is asking
    /// for all of it, and a partial purge is not a purge. It is the one operation that is not paged,
    /// and the only one that takes a tenant id rather than a <see cref="DocketScope"/> — there is no
    /// such thing as purging half a tenant.
    /// </remarks>
    Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>Every entry in <paramref name="scope"/>, in filing order, streamed.</summary>
    /// <param name="scope">What to export.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <remarks>
    /// An <see cref="IAsyncEnumerable{T}"/> rather than a list so a large Docket never has to fit in
    /// memory. The portable document shape a host would export <em>to</em> is not fixed by this
    /// release; this yields the rows.
    /// </remarks>
    IAsyncEnumerable<DocketEntry> ExportAsync(DocketScope scope, CancellationToken ct);
}
