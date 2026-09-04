namespace Affiant.Core.Services;

using System.Diagnostics;
using Affiant.Abstractions;
using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Observability;
using Microsoft.Extensions.Logging;

/// <summary>
/// Single source of truth for the review state machine: file a <see cref="DocketEntry"/>,
/// evaluate the approval policy, optionally send an <see cref="EvidenceCardRequest"/>
/// and await the reviewer's response, then update the final status.
/// </summary>
public sealed class ReviewGate(
    IStreamingTransport transport,
    IDocketStore docketStore,
    IApprovalPolicyEvaluator evaluator,
    AffiantCoreOptions options,
    ILogger<ReviewGate> logger,
    TimeProvider? timeProvider = null,
    IDecisionAuthorizationPolicy? decisionAuthorization = null,
    ToolCoverage? coverage = null)
{
    /// <summary>
    /// The tools the host has declared the gate cannot stand in front of (CV-4), or
    /// <see langword="null"/> when it declared none. An entry filed for one of them is blocked with
    /// the category, so no decision on it is ever accepted and the card says why.
    /// </summary>
    private readonly ToolCoverage? _coverage = coverage;

    /// <summary>
    /// The gate's only clock. Every instant it stamps (<c>DocketEntry.CreatedAt</c>,
    /// <c>ExpiresAt</c>, a resubmission's <c>WriteProposal</c>) and every deadline comparison it
    /// makes reads from here, so a host or a test can move time without moving the machine's.
    /// Defaults to <see cref="TimeProvider.System"/> — <c>AddAffiantCore</c> registers exactly that
    /// as the DI default, so a host that does nothing sees no change.
    /// </summary>
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Who may decide, report on or resubmit an entry (AZ-2).
    /// </summary>
    /// <remarks>
    /// <b>The fallback denies.</b> A host that registered no
    /// <see cref="IDecisionAuthorizationPolicy"/> gets <see cref="DenyAllDecisionAuthorization"/>,
    /// so there is no window — not even the one before startup validation runs — in which the gate
    /// admits a decision nobody vouched for. <c>AffiantWireUpValidator</c> refuses at startup when
    /// this application declares a write-capable tool and no policy is registered, so a host does
    /// not silently run on the deny-all; it is the safe floor, not a configuration to ship.
    /// </remarks>
    private readonly IDecisionAuthorizationPolicy authorization =
        decisionAuthorization ?? DenyAllDecisionAuthorization.Instance;

    /// <summary>
    /// Non-blocking half of filing a review (framework enabler for host issue
    /// affiant-host-apps#25 / triage F0-A1): files the <see cref="DocketEntry"/>, evaluates the
    /// approval policy (auto-approve/referral short-circuits included), and — if a human reviewer
    /// must act — broadcasts the <see cref="EvidenceCardRequest"/>, all without registering a
    /// waiter or blocking on the reviewer's response. Use this when the caller cannot afford to
    /// await a review inline (e.g. a host request pipeline); route the eventual decision to
    /// <c>HandleDecisionAsync</c> when it arrives.
    /// </summary>
    /// <param name="proposal">The proposed write operation awaiting review.</param>
    /// <param name="context">Session, tenant, user, and affidavit context for routing the review.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>
    /// <see cref="ReviewFilingResult.RequiresReview"/> if a reviewer must act, or
    /// <see cref="ReviewFilingResult.Decided"/> if the review was already settled (StandingOrder
    /// auto-approval, ReferralRequired escalation, or idempotent replay).
    /// </returns>
    public Task<ReviewFilingResult> FileForReviewAsync(
        WriteProposal proposal,
        ReviewContext context,
        CancellationToken cancellationToken = default)
        => FileForReviewCoreAsync(proposal, context, cancellationToken);

    /// <summary>
    /// <b>Obsolete (<c>AFFIANT0002</c>), kept for one release.</b> Files a review for
    /// <paramref name="proposal"/> and blocks until the outcome is known. Use
    /// <see cref="FileForReviewAsync"/> to file and <see cref="HandleDecisionAsync"/> to decide.
    /// <para>
    /// <b>Why it is going.</b> When policy requires a person, this method awaits
    /// <see cref="IStreamingTransport.AwaitEvidenceCardResponseAsync"/> on the SAME call chain the
    /// caller's own connection is holding open. Over SignalR — the framework's only shipped
    /// transport — <c>HubOptions.MaximumParallelInvocationsPerClient</c> defaults to <c>1</c>, so the
    /// one hub invocation that could deliver the reviewer's decision queues behind the very
    /// invocation blocked here awaiting it: a same-connection deadlock proven live (host-apps#25,
    /// Jaeger-traced 610.7s block). Every call that requires human review waits out
    /// <see cref="AffiantCoreOptions.DefaultDocketTtl"/> and resolves as
    /// <see cref="ReviewOutcome.Expired"/> under that condition — not because the reviewer failed to
    /// act, but because their decision cannot physically reach this awaiting call.
    /// </para>
    /// <para>
    /// <b>It decides nothing.</b> This call waits for a <see cref="DecisionHandOff"/> and reports
    /// it. Every decision — the principal, the tenant-scoped row, the host's authorization port, the
    /// state and blocked checks, and the attestation — runs in <see cref="HandleDecisionAsync"/>,
    /// which has already written the row by the time a hand-off exists (AZ-1, AZ-2). What this path
    /// still owns is the timeout: a review nobody answered inside the window expires here, and only
    /// the caller whose own compare-and-set won that transition reports it.
    /// </para>
    /// </summary>
    /// <param name="proposal">The proposed write operation awaiting review.</param>
    /// <param name="context">Session, tenant, user, and affidavit context for routing the review.</param>
    /// <param name="cancellationToken">Caller cancellation — distinct from the internal timeout.</param>
    /// <returns>
    /// <see cref="ReviewOutcome.Approved"/>, <see cref="ReviewOutcome.Rejected"/>,
    /// <see cref="ReviewOutcome.Expired"/>, or <see cref="ReviewOutcome.Referral"/>.
    /// </returns>
    [Obsolete(
        "Blocking review is retired: it deadlocks over the only shipped transport. File with " +
        "FileForReviewAsync and decide with HandleDecisionAsync. This member is kept for one " +
        "release.",
        DiagnosticId = "AFFIANT0002")]
    public async Task<ReviewOutcome> FileReviewAsync(
        WriteProposal proposal,
        ReviewContext context,
        CancellationToken cancellationToken = default)
    {
        var filing = await FileForReviewAsync(proposal, context, cancellationToken);
        if (filing is ReviewFilingResult.Decided decided)
            return decided.Outcome;

        var entryId = ((ReviewFilingResult.RequiresReview)filing).EntryId;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(options.DefaultDocketTtl);
            var handOff = await transport.AwaitEvidenceCardResponseAsync(
                context.SessionId, entryId, cts.Token);
            logger.LogInformation(
                "Received a decision hand-off for DocketEntry {EntryId}: {Decision}",
                entryId, handOff.Decision);

            // The row is already written, attested and reported. Nothing is decided here.
            return handOff.Outcome;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout (internal CTS fired, not the caller).
            logger.LogWarning(
                "EvidenceCardRequest timed out for DocketEntry {EntryId} after {Minutes} minutes",
                entryId, options.DefaultDocketTtl.TotalMinutes);

            // The guarded, scoped transition every other write on this row goes through. Anything
            // but Transitioned means a decision claimed the entry a beat earlier: report and leave
            // untouched the status it genuinely landed in, so we never tell the session group
            // something that did not happen. Expiry carries no attestation because nobody decided
            // it, which is the one case the attestation guard exempts (AZ-1).
            var expiry = await docketStore.TransitionAsync(
                entryId,
                new DocketScope(context.TenantId),
                ReviewStatus.Pending,
                new DocketTransitionPatch(ReviewStatus.Expired),
                cancellationToken);

            if (expiry is not DocketTransitionResult.Transitioned)
            {
                var finalEntry = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
                return finalEntry is null
                    ? new ReviewOutcome.Expired(entryId)
                    : finalEntry.Status.ToReviewOutcome(entryId);
            }

            RecordTransitionIfWon(1, entryId, context.SessionId, ReviewStatus.Expired);

            await transport.BroadcastToGroupAsync(
                context.SessionId, TransportEvent.DocketExpired,
                new DocketExpiredNotification(entryId), cancellationToken);
            return new ReviewOutcome.Expired(entryId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "FileReviewAsync cancelled awaiting decision for tool {ToolName}", proposal.ToolName);
            throw;
        }
    }
    /// <summary>
    /// The reviewer act a resubmission prefills from: when the corrections were made and by whom.
    /// </summary>
    /// <remarks>
    /// Preserved amendments carry both, because the gate wrote them from the decision it refused as
    /// late. A row whose corrections were accepted instead carries them on the decision and the
    /// attestation. Where neither says who acted, nothing is prefilled onto the record: a tag naming
    /// an unknown person is worse than no tag, and the map beside the card still shows the values.
    /// </remarks>
    private static (DateTimeOffset At, string By)? ReviewerActOf(DocketEntry entry) =>
        entry.PreservedAmendments is { } preserved
            ? (preserved.At, preserved.By)
            : entry.Attestation is { } attestation && entry.DecidedAt is { } decidedAt
                ? (decidedAt, attestation.By.Subject)
                : null;


    /// <summary>
    /// Resubmits an expired review for a fresh reviewer round (framework half of repo issue #9):
    /// files a brand-new Pending <see cref="DocketEntry"/> (new <see cref="DocketEntry.EntryId"/>,
    /// fresh TTL) cloning the expired entry's envelope/affidavit via <see cref="FileForReviewAsync"/>,
    /// and broadcasts its Evidence Card carrying the original entry's persisted
    /// <see cref="DocketEntry.Amendments"/> in <see cref="EvidenceCardRequest.PriorAmendments"/> so
    /// the reviewer sees what was already agreed before the window lapsed.
    /// </summary>
    /// <param name="expiredEntryId">The <see cref="DocketEntry.EntryId"/> of the expired entry to resubmit.</param>
    /// <param name="context">Who is resubmitting, and in which tenant.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>
    /// The <see cref="ReviewFilingResult"/> for the fresh entry — see <see cref="FileForReviewAsync"/>
    /// — or <see cref="ReviewFilingResult.Decided"/> carrying a
    /// <see cref="ReviewOutcome.Refused"/> when the caller may not resubmit this entry.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The entry's <see cref="DocketEntry.Status"/> is not <see cref="ReviewStatus.Expired"/>, or a
    /// concurrent <see cref="ResubmitAsync"/> call already claimed it (see remarks). An entry whose
    /// deadline has passed reads as <see cref="ReviewStatus.Expired"/> whether or not the sweep has
    /// reached it, so a resubmission never has to wait for a sweep tick.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The same authorization checks a decision runs</b> (AZ-2): an unresolved principal is
    /// refused before the Docket is read, an entry outside the caller's tenant is <em>not found</em>,
    /// and the host's authorization port has the last word. A resubmission re-opens a review window
    /// on somebody's behalf; a caller that could not have decided the entry cannot re-open it either.
    /// </para>
    /// <para>
    /// <b>Lineage and the race guard (Area-5 Decision 2, affiant#31):</b> <see cref="DocketEntry.ResubmittedTo"/>
    /// on the <paramref name="expiredEntryId"/> entry is both the atomic guard that stops two
    /// concurrent resubmissions of the same entry from both minting a fresh <see cref="DocketEntry"/>,
    /// and the queryable lineage record of what it became. There is deliberately no
    /// <c>ReviewStatus.Resubmitted</c> — the source entry's <see cref="DocketEntry.Status"/> stays
    /// <see cref="ReviewStatus.Expired"/> forever, matching the client's own shipped decision to
    /// never visually distinguish a resubmitted card from a plain expired one. The new entry's id is
    /// minted up front (<see cref="Guid.NewGuid"/>) precisely so <see cref="IDocketStore.ConsumeForResubmitAsync"/>
    /// and the eventual filing both target the same, already-known id.
    /// </para>
    /// <para>
    /// <b>Ordering and its failure mode:</b> the guard runs <i>before</i> filing — <see cref="IDocketStore.ConsumeForResubmitAsync"/>
    /// is the operation two concurrent callers actually race on, not the filing itself. The loser
    /// sees 0 rows affected and throws <see cref="InvalidOperationException"/>, the same shape as
    /// the not-found/not-Expired guards above (mirrored by hosts' existing "already processed or
    /// expired" handling for this method — no new client behavior needed). The cost of guarding
    /// first: if filing the new entry fails afterward (store outage, cancellation) the winning
    /// caller's consume already committed, so <paramref name="expiredEntryId"/>'s <c>ResubmittedTo</c>
    /// is left pointing at an <see cref="DocketEntry.EntryId"/> that was never actually filed — a
    /// permanently orphaned lineage pointer, since the guard is one-shot and the source entry can
    /// never be consumed again. This is documented, not compensated: no automatic rollback clears
    /// <c>ResubmittedTo</c> on filing failure, because a rollback would itself need to race safely
    /// against a subsequent resubmit attempt, reopening the exact problem the guard exists to close.
    /// The failure is logged at <see cref="Microsoft.Extensions.Logging.LogLevel.Error"/> with both
    /// ids for manual operator follow-up — the same accepted, logged-not-recovered shape as
    /// <see cref="BroadcastEvidenceCardWithRetryAsync"/>'s residual risk.
    /// </para>
    /// </remarks>
    public async Task<ReviewFilingResult> ResubmitAsync(
        Guid expiredEntryId,
        DecisionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (RequirePrincipal(context, expiredEntryId, ResubmitPath) is not { } principal)
            return new ReviewFilingResult.Decided(Unauthorized(expiredEntryId, AuthorizationRule));

        var entry = await RequireEntryAsync(expiredEntryId, context, ResubmitPath, cancellationToken);
        if (entry is null)
        {
            return new ReviewFilingResult.Decided(
                new ReviewOutcome.Refused(expiredEntryId, DocketRefusalCodes.EntryNotFound));
        }

        if (!await IsAuthorizedAsync(principal, entry, context, ResubmitPath, cancellationToken))
            return new ReviewFilingResult.Decided(Unauthorized(expiredEntryId, AuthorizationRule));

        if (entry.Status != ReviewStatus.Expired)
        {
            throw new InvalidOperationException(
                $"ResubmitAsync: DocketEntry {expiredEntryId} is {entry.Status}, expected Expired.");
        }

        var scope = new DocketScope(entry.TenantId);
        var newEntryId = Guid.NewGuid();

        // Claim the source entry for newEntryId before filing anything else — the claim, not the
        // filing, is what two concurrent ResubmitAsync calls for the same expired entry actually race
        // on, and the same write records the lineage. The guard admits a row that READS expired,
        // which is either a persisted Expired or one whose deadline passed before the sweep reached
        // it, so a resubmission never has to wait for a sweep tick. See this method's remarks for the
        // ordering trade-off the claim-first shape implies.
        var supersession = await docketStore.RecordSupersessionAsync(
            expiredEntryId, scope, newEntryId, cancellationToken);
        if (supersession is not RecordSupersessionResult.Recorded)
        {
            throw new InvalidOperationException(
                $"ResubmitAsync: DocketEntry {expiredEntryId} was already resubmitted by a concurrent caller.");
        }

        // The new proposal is prefilled with what the reviewer had already corrected. Two facts can
        // carry that, and they are not the same fact: PreservedAmendments is what a decision the gate
        // REFUSED as late carried — nobody accepted it — while Amendments is what an approval
        // accepted. A resubmission prefills from the first when it exists, because that is the
        // reviewer's own uncommitted correction, and falls back to the second for a row whose
        // corrections were recorded before the two were told apart.
        //
        // Read back TYPED, both of them. A row that has been through a store carries every field
        // value and every amendment as raw JSON, and a resubmission is the one path that always
        // reads the record back out: a host risk scorer that pattern-matches on a value's type would
        // otherwise see an unrecognised type for every field and fall through to its default branch,
        // so the same content would score one way when first filed and another way when resubmitted.
        var prefill = AffidavitFieldValues.Typed(
            entry.PreservedAmendments?.Amendments ?? entry.Amendments);
        var priorAmendments = prefill is { Count: > 0 } ? prefill : null;
        var sworn = AffidavitFieldValues.Typed(entry.Envelope);

        // Prefilled ON THE RECORD, not only in a map beside it. A resubmission that re-proposed the
        // machine's original values would ask the reviewer to make the same correction a second
        // time, and the card would show the value they had already rejected. Each corrected field
        // carries the reviewer's own tag, bound to the decision they made it on (PV-2), with the
        // tag it displaces kept beneath it (AF-4). A field they cleared stays on the card, empty,
        // rather than vanishing — see AffidavitAmendments.Prefill.
        if (priorAmendments is not null && ReviewerActOf(entry) is var (actAt, actBy))
        {
            sworn = AffidavitAmendments.Prefill(sworn, priorAmendments, expiredEntryId, actAt, actBy);
        }

        var proposal = new WriteProposal(entry.ToolName, _time.GetUtcNow(), sworn);
        var filing = new ReviewContext(
            SessionId: entry.SessionId,
            TenantId: entry.TenantId,
            UserId: entry.UserId,
            // DocketEntry.ReviewerUserId is null for self-reviewed entries (see DocketEntry
            // remarks); ReviewContext requires a non-null reviewer, so self-review falls back
            // to the original proposer.
#pragma warning disable AFFIANT0001 // superseded by the attestation; still the routing hint until then
            ReviewerUserId: entry.ReviewerUserId ?? entry.UserId,
#pragma warning restore AFFIANT0001
            Affidavit: sworn,
            EntryId: newEntryId,
            Amendments: priorAmendments,
            Channel: context.Channel,
            // The other half of the lineage. The successor link was written on the superseded row
            // above; this is what the new row records about where it came from, so the history reads
            // forward from either end without a reverse lookup.
            Supersedes: expiredEntryId);

        ReviewFilingResult result;
        try
        {
            result = await FileForReviewCoreAsync(proposal, filing, cancellationToken);
        }
        catch (Exception ex)
        {
            // See method remarks: the claim above already committed the successor link on the source
            // entry. A filing failure here orphans that pointer — documented, not compensated.
            // Deliberately catches OperationCanceledException too (not just other exceptions): a
            // connection-tied token cancels FileForReviewCoreAsync exactly as readily as it throws,
            // and the orphan is identical either way, so the operator-follow-up signal this log
            // exists for must not go dark just because the cause was cancellation.
            logger.LogError(ex,
                "ResubmitAsync: DocketEntry {ExpiredEntryId} was claimed for resubmission as " +
                "{NewEntryId}, but filing the new entry failed — the lineage now names an entry " +
                "that was never filed",
                expiredEntryId, newEntryId);
            throw;
        }

        logger.LogInformation(
            "ResubmitAsync: resubmitted expired DocketEntry {ExpiredEntryId} as fresh DocketEntry " +
            "{NewEntryId} ({FilingResultType})",
            expiredEntryId, newEntryId, result.GetType().Name);

        return result;
    }

    /// <summary>
    /// Reconnect-side counterpart to <c>DocketExpiryService</c>'s sweep (Area-5 Decision 3
    /// acceptance criterion 2, affiant#28): re-broadcasts an <see cref="EvidenceCardRequest"/> for
    /// every entry <see cref="IDocketStore.ListPendingBySessionAsync"/> currently reports Pending in
    /// <paramref name="sessionId"/>, oldest-filed first. Intended to be called from a host's
    /// <c>RehydrateSession</c> hub RPC so a reconnecting client gets its stranded cards immediately
    /// rather than waiting up to 30s for the next sweep tick — wiring that call site is host-wave
    /// scope, not this method's.
    /// </summary>
    /// <remarks>
    /// Uses the same <see cref="BroadcastEvidenceCardWithRetryAsync"/> retry/telemetry path the
    /// filing-time broadcast uses, and the same <see cref="EvidenceCardRequestFactory"/> the sweep
    /// uses, so a reconnect rebroadcast is indistinguishable in shape from either. Per the D3
    /// ruling's explicit sign-off boundary: this closes "the client gets the card again on
    /// reconnect," not "a human has seen it" — see <see cref="EvidenceCardRequest"/>'s docs.
    /// </remarks>
    public async Task RebroadcastPendingCardsAsync(
        string sessionId, string tenantId, CancellationToken cancellationToken)
    {
        var scope = new DocketScope(tenantId, sessionId);
        string? cursor = null;

        // Paged, not "every pending entry in one read": a session with a long stranded backlog is
        // exactly the session a reconnect has to serve, and a rebroadcast that loaded the whole
        // backlog into memory to do it would fail hardest where it matters most.
        do
        {
            var page = await docketStore.ListPendingAsync(
                scope, new DocketPage(RebroadcastPageSize, cursor), cancellationToken);

            foreach (var entry in page.Items)
            {
                var request = await EvidenceCardRequestFactory.CreateAsync(
                    docketStore, entry.EntryId, entry.Envelope, entry.ExpiresAt, cancellationToken);
                await BroadcastEvidenceCardWithRetryAsync(
                    sessionId, entry.EntryId, entry.ToolName, request, cancellationToken);
            }

            cursor = page.Cursor;
        }
        while (cursor is not null);
    }

    /// <summary>How many stranded cards one reconnect rebroadcast reads at a time.</summary>
    private const int RebroadcastPageSize = 50;

    /// <summary>
    /// Shared filing core for <see cref="FileForReviewAsync"/> and <see cref="ResubmitAsync"/>, run
    /// in the order the protocol fixes (rule GT-1): <b>runtime substance refusal → idempotent
    /// replay → the approval-policy chain → the deadline stamped from what the chain returned →
    /// filed</b>, and — when a human reviewer must act — the Evidence Card broadcast. Never
    /// registers a waiter and never blocks on a reviewer response.
    ///
    /// <para>
    /// <b>What changed and why (GT-1, GT-3, GT-4).</b> Until <c>1.0.0-beta.1</c> this method filed
    /// the row first, with a deadline computed from one process-wide default, and evaluated the
    /// policy chain afterwards. Two rules were unreachable in that order. A policy could not name a
    /// review window, because the window was already stamped by the time the policy spoke — so "five
    /// minutes for a high-risk write, a day for a routine one" had nowhere to be said. And nothing
    /// checked whether the proposal swore to anything before a reviewer was asked about it: the
    /// substance rule lived only in the compliance harness, which runs in an adopter's test suite
    /// and never in production. Both are closed here, in the order the rule states, so a proposal
    /// that swears to nothing never reaches a policy and a deadline is never stamped before one.
    /// </para>
    ///
    /// <para>
    /// <b>A requirement level this version does not run is blocked, never degraded</b> (AZ-4).
    /// <see cref="ReviewRequirement.ReferralRequired"/> and <see cref="ReviewRequirement.MultiParty"/>
    /// file the entry pending with a <see cref="BlockedMarker"/> recording the level verbatim; every
    /// decision on such an entry is refused and it never reaches an executor.
    /// </para>
    /// </summary>
    /// <exception cref="AffiantSubstanceException">
    /// The proposal swears to nothing (GT-3). Nothing was filed and nothing was broadcast.
    /// </exception>
    /// <exception cref="AffiantPolicyException">
    /// A policy named a review window that is not a deadline, or threw (CV-1). Nothing was filed.
    /// </exception>
    private async Task<ReviewFilingResult> FileForReviewCoreAsync(
        WriteProposal proposal,
        ReviewContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);

        // GT-3, first and outside the try: a proposal that swears to nothing is refused before any
        // store is touched, so the refusal cannot be confused with a filing failure.
        RefuseProposalWithoutSubstance(proposal, context);

        // GT-4: an entry id is derived from the proposal, not invented. The same proposal in the
        // same conversation replays to the same row, and two tenants cannot collide by accident —
        // which is what makes the scoped replay lookup below safe to treat a miss as a fresh filing.
        var entryId = context.EntryId ?? DeriveEntryId(context, proposal);

        // One instant for the whole filing: CreatedAt and ExpiresAt must name the same "now", or a
        // test that pins the clock sees a TTL that is off by however long the filing took.
        var now = _time.GetUtcNow();

        try
        {
            // 1. An existing entry with this id is an idempotent replay, never a second entry and
            //    never an error (GT-4, DK-1). A terminal one reports its state; a blocked one
            //    reports the marker; a pending one re-broadcasts ITS OWN card with ITS OWN deadline.
            //    Never a fresh one: a reviewer shown a deadline the record does not hold is being
            //    shown a lie, and the retry would silently extend a window the first filing set.
            var existing = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);

            // GT-2: the lookup is scoped. A row in another tenant is not this caller's replay and
            // must not be read, re-broadcast or reported: broadcasting it would put another
            // tenant's Affidavit on this caller's session group, and reporting its status would be
            // an existence oracle for any id in the deployment. A scoped miss is a fresh filing,
            // and the store refuses the duplicate id if one really is taken.
            if (existing is not null
                && !string.Equals(existing.TenantId, context.TenantId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "DocketEntry {EntryId} is outside the filing tenant; the filing proceeds as a new " +
                    "entry rather than replaying a row this caller may not see",
                    entryId);
                existing = null;
            }

            if (existing is not null)
            {
                // TL-1 `affidavit.filed` with created=false: a host that retries a proposal wants to
                // see the retry — an event that only fired on the first filing would make a retry
                // storm invisible.
                AffiantTelemetry.RecordAffidavitFiled(
                    proposal.ToolName,
                    context.SessionId,
                    entryId,
                    DocketStateName(existing.Status),
                    existing.Envelope.Fields.Length,
                    created: false);

                if (existing.Blocked is not null)
                    return new ReviewFilingResult.Decided(RefuseBlocked(entryId, existing.Blocked));

                if (existing.Status != ReviewStatus.Pending)
                    return new ReviewFilingResult.Decided(existing.Status.ToReviewOutcome(entryId));

                var replay = await EvidenceCardRequestFactory.CreateAsync(
                    docketStore, entryId, existing.AmendedAffidavit ?? existing.Envelope,
                    existing.ExpiresAt, cancellationToken, blocked: existing.Blocked);
                await BroadcastEvidenceCardWithRetryAsync(
                    context.SessionId, entryId, existing.ToolName, replay, cancellationToken);

                logger.LogInformation(
                    "Replayed DocketEntry {EntryId} for tool {ToolName}: still pending, re-broadcast " +
                    "with its existing deadline {ExpiresAt}",
                    entryId, proposal.ToolName, existing.ExpiresAt);
                return new ReviewFilingResult.RequiresReview(entryId);
            }

            // 2. The approval-policy chain, BEFORE the row is filed (GT-1). The chain walks the
            //    policies in registration order, takes the first non-null verdict, applies the GT-5
            //    and PV-4 checks to it, and defaults to ReviewerConfirmation when none speaks. A
            //    policy that names an unusable window or throws refuses here with nothing filed.
            var verdict = await evaluator.EvaluateAsync(
                context.Affidavit, IdentityOf(context, now), cancellationToken);
            var requirement = verdict.Requirement;

            // 3. The deadline, stamped from the policy result and only now (GT-4): the verdict's own
            //    window, else the policy's declared default (the chain already folded that in), else
            //    this gate's. One global default applied before the policy chain is what the rule
            //    calls non-conformant, and it is what this method used to do. Stamped from the same
            //    instant CreatedAt is, so a pinned clock sees the window it asked for.
            var expiresAt = now.Add(verdict.TimeToLive ?? options.DefaultDocketTtl);

            // What the record says in words, beyond what it swears to.
            //
            // GT-5: when a Standing Order was held back, the reason it was held back is the reason a
            // person is being asked, and it belongs on the card they are being asked on. A reviewer
            // shown a confirmation with no explanation cannot tell an ordinary review from one a
            // policy escalated, which is the difference that decides how carefully they read it.
            //
            // AZ-4: a blocked entry says in words why no decision on it will be accepted. The marker
            // is the structured fact and a surface can render it, but one that renders warnings and
            // not markers would otherwise show a card with no decision available and no explanation.
            //
            // Both go on the ROW, not just the card: the card reports the record, and a card
            // carrying a sentence the row does not is a card that disagrees with what was filed.
            var notes = new List<string>(context.Affidavit.Warnings);
            if (verdict.DegradedFrom is not null && verdict.Reason is { Length: > 0 } held)
                notes.Add(held);

            // CV-4: a tool the host declared it cannot cover says so on the row and on the card, in
            // words as well as in the marker. A surface that rendered markers and not warnings would
            // otherwise show a card with no decision available and no explanation.
            var coverageMarker = _coverage?.MarkerFor(proposal.ToolName);
            if (coverageMarker is not null)
            {
                notes.Add(
                    $"The tool '{proposal.ToolName}' is declared uncovered " +
                    $"({ToolCoverage.Spell(coverageMarker.Category)}): the gate cannot stand in " +
                    "front of the write it makes, so this entry is blocked and no decision on it " +
                    "will be accepted.");
            }

            if (requirement is ReviewRequirement.ReferralRequired or ReviewRequirement.MultiParty)
            {
                notes.Add(
                    $"{requirement} is a requirement level this release records verbatim but does " +
                    "not run: it is not implemented in this version, so no decision on this entry " +
                    "will be accepted.");
            }

            // The record is stamped with the gate's own clock as it is filed. A record that cannot
            // say when it was built cannot be told from one built at another moment, and a
            // reviewer's accepted amendment is dated against it (SR-3, PV-2). One instant for the
            // whole filing, the same one the deadline is measured from. A record that arrives
            // already stamped — a host's own projection, or a replay — keeps what it says.
            var stamped = context.Affidavit.CreatedAt is null
                ? context.Affidavit with { CreatedAt = now }
                : context.Affidavit;

            var sworn = notes.Count == stamped.Warnings.Length
                ? stamped
                : stamped with { Warnings = [.. notes] };

            // 4. File (DK-1).
            //    Same shape as DocketEntry.Amendments since the Area-8 amendments unification —
            //    no copy or value-type widening needed on the way in.
            var amendments = context.Amendments is { Count: > 0 } ? context.Amendments : null;

            var entry = new DocketEntry(
                EntryId: entryId,
                SessionId: context.SessionId,
                TenantId: context.TenantId,
                UserId: context.UserId,
                ReviewerUserId: context.ReviewerUserId,
                OperationType: proposal.ToolName,
                Envelope: sworn,
                Status: ReviewStatus.Pending,
                CreatedAt: now,
                ExpiresAt: expiresAt,
                Amendments: amendments,
                Supersedes: context.Supersedes,
                ProtocolVersion: AffiantProtocol.Version)
            {
                ToolName = proposal.ToolName,
                Channel = context.Channel,
                Requirement = requirement,
            };
            await docketStore.FileDocketEntryAsync(entry, cancellationToken);
            logger.LogInformation(
                "Filed DocketEntry {EntryId} for tool {ToolName} as {Requirement}, deadline {ExpiresAt}",
                entryId, proposal.ToolName, requirement, expiresAt);

            // TL-1 `affidavit.filed`. `docket.requirement` is present now that the chain runs before
            // the filing: the event says what the row was filed as, not a guess.
            AffiantTelemetry.RecordAffidavitFiled(
                proposal.ToolName,
                context.SessionId,
                entryId,
                DocketStateName(ReviewStatus.Pending),
                context.Affidavit.Fields.Length,
                created: true,
                requirement: requirement.ToString());

            // 5a. StandingOrder: auto-approve without client interaction — and attest it (AZ-1).
            //
            // The attestation is written in the SAME operation that files the entry approved, so
            // there is no window in which an approved write has no attribution. Nobody decided, so
            // there is no decision record and no person to name: what the row records is the policy
            // that fired and the version of it that fired, which is the honest answer to "who
            // approved this" for a write approved with no person present.
            if (requirement == ReviewRequirement.StandingOrder && coverageMarker is null)
            {
                var attestation = new Attestation(
                    Attestor.StandingOrder.Of(
                        verdict.PolicyId ?? UnnamedStandingOrderPolicy, verdict.PolicyVersion),
                    now,
                    entryId);

                var approved = await docketStore.TransitionAsync(
                    entryId,
                    new DocketScope(context.TenantId),
                    ReviewStatus.Pending,
                    new DocketTransitionPatch(
                        ReviewStatus.Approved, Attestation: attestation, DecidedAt: now),
                    cancellationToken);

                if (approved is DocketTransitionResult.Transitioned transitioned)
                {
                    AffiantTelemetry.RecordDocketTransition(
                        entryId,
                        context.SessionId,
                        DocketStateName(ReviewStatus.Pending),
                        DocketStateName(ReviewStatus.Approved),
                        execution: ExecutionStateName(transitioned.Entry.Execution),
                        attestationKind: attestation.By.Kind);
                }

                // TL-1 `standing-order.fired` (AZ-1): a write was approved with no person present,
                // which is the single most consequential thing a policy can do and the one an
                // operator most needs to be able to count. Emitted here rather than inside the
                // policy because this is where the approval actually happens — the entry exists, so
                // the event can name it, and a verdict a later check degraded never reaches here.
                AffiantTelemetry.RecordStandingOrderFired(
                    verdict.PolicyId ?? UnnamedStandingOrderPolicy,
                    verdict.RiskScore,
                    entryId,
                    verdict.PolicyVersion);

                logger.LogInformation(
                    "StandingOrder {PolicyId} auto-approved DocketEntry {EntryId}",
                    attestation.By.Subject, entryId);

                // SR-4: the card still goes out, and it says no confirmation is needed. A person was
                // not asked, which is exactly why the reviewer surface has to be told what was
                // approved in their name — a write that appears on no card is a write nobody can
                // see. Built through the same factory and sent down the same retry path as every
                // other branch, so the three cannot drift.
                var approvedCard = await EvidenceCardRequestFactory.CreateAsync(
                    docketStore, entryId, sworn, expiresAt, cancellationToken,
                    requiresConfirmation: false);
                await BroadcastEvidenceCardWithRetryAsync(
                    context.SessionId, entryId, proposal.ToolName, approvedCard, cancellationToken);

                return new ReviewFilingResult.Decided(new ReviewOutcome.Approved(entryId));
            }

            // 5a-bis. A tool the host declared uncovered (CV-4). The entry is filed — the proposal
            // happened and the record of it is the point — and it is blocked with the category, so
            // no decision on it is ever accepted and it can never reach an executor. It is NOT
            // auto-approved, whatever the policy said: a Standing Order approves a write the gate
            // stands in front of, and this is a write the gate has been told it cannot.
            if (coverageMarker is not null)
            {
                await docketStore.MarkBlockedAsync(
                    entryId, new DocketScope(context.TenantId), coverageMarker, cancellationToken);

                AffiantTelemetry.RecordCoverageRefused(
                    proposal.ToolName, ToolCoverage.Spell(coverageMarker.Category), "filing");

                logger.LogWarning(
                    "DocketEntry {EntryId} is blocked: tool {ToolName} is declared uncovered " +
                    "({Category}), so no decision on it can be accepted",
                    entryId, proposal.ToolName, coverageMarker.Category);

                var uncoveredCard = await EvidenceCardRequestFactory.CreateAsync(
                    docketStore, entryId, sworn, expiresAt, cancellationToken,
                    blocked: coverageMarker);
                await BroadcastEvidenceCardWithRetryAsync(
                    context.SessionId, entryId, proposal.ToolName, uncoveredCard, cancellationToken);

                return new ReviewFilingResult.Decided(RefuseBlocked(entryId, coverageMarker));
            }

            // 5b. A requirement level this version records but does not run — ReferralRequired and
            // MultiParty, whose semantics are reserved. The level is recorded VERBATIM, the entry
            // stays pending carrying a blocked marker, every decision on it is refused, and it is
            // never degraded to a weaker requirement.
            //
            // What this replaces is the failure the rule exists to prevent: MultiParty used to fall
            // through to the single-card branch below, so a write that needed several parties' joint
            // approval was silently satisfied by one person clicking approve; and ReferralRequired
            // used to write a Deferred status naming a transition no implementation has ever run.
            if (requirement is ReviewRequirement.ReferralRequired or ReviewRequirement.MultiParty)
            {
                var blocked = new BlockedMarker.RequirementNotImplemented(requirement);
                await docketStore.MarkBlockedAsync(
                    entryId, new DocketScope(context.TenantId), blocked, cancellationToken);
                logger.LogWarning(
                    "DocketEntry {EntryId} is blocked: requirement {Requirement} is recorded but not " +
                    "implemented in this version, so no decision on it can be accepted",
                    entryId, requirement);

                // The card still goes out, and it says on its face that the entry is blocked — a
                // blocked entry never claims a confirmation is being awaited.
                var blockedRequest = await EvidenceCardRequestFactory.CreateAsync(
                    docketStore, entryId, sworn, expiresAt, cancellationToken,
                    blocked: blocked);
                await BroadcastEvidenceCardWithRetryAsync(
                    context.SessionId, entryId, proposal.ToolName, blockedRequest, cancellationToken);

                return new ReviewFilingResult.Decided(RefuseBlocked(entryId, blocked));
            }

            // 5c. ReviewerConfirmation: send the Evidence Card. No waiter registered here — that is
            // the caller's choice (FileReviewAsync awaits it; FileForReviewAsync callers route the
            // eventual decision through HandleDecisionAsync instead). Built via the shared factory
            // (Area-5 Decision 3, affiant#28) so this payload and the sweep's re-broadcast payload
            // for the same entry cannot drift — including PriorAmendments, re-derived here via the
            // same resubmission reverse-lookup rather than threaded through as a parameter, so
            // ResubmitAsync's own filing call needs no special case.
            var request = await EvidenceCardRequestFactory.CreateAsync(
                docketStore, entryId, sworn, expiresAt, cancellationToken);
            await BroadcastEvidenceCardWithRetryAsync(
                context.SessionId, entryId, proposal.ToolName, request, cancellationToken);

            return new ReviewFilingResult.RequiresReview(entryId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("FileForReviewAsync cancelled for tool {ToolName}", proposal.ToolName);
            throw;
        }
    }

    /// <summary>
    /// Broadcasts the Evidence Card for a DocketEntry that is already durably filed as
    /// <see cref="ReviewStatus.Pending"/> — retrying once on failure (P1a, affiant#22 / FV-9).
    ///
    /// <para>
    /// Filing has already succeeded by the time this runs: the caller reports success (
    /// <see cref="ReviewFilingResult.RequiresReview"/>) regardless of whether the broadcast itself
    /// ultimately succeeds. Reporting failure here would be a lie — the proposal genuinely IS filed
    /// and pending, discoverable via <see cref="IDocketStore.ListPendingBySessionAsync"/> — and would
    /// invite a caller to re-file on "failure", creating a duplicate docket entry for the same
    /// proposal. If both the initial broadcast and the single retry fail, the entry is left durably
    /// Pending but orphaned from the push-notification path: it is logged (<see cref="LogLevel.Error"/>),
    /// an <c>affiant.review.broadcast_failed</c> OTel event is emitted, and a best-effort
    /// <see cref="TransportEvent.SystemNotification"/> is broadcast so operators and the client both
    /// have a chance to notice.
    /// </para>
    /// <para>
    /// <b>Residual risk (documented, not fixed here):</b> <see cref="DocketEntry"/> has no field to
    /// persist a "broadcast failed" marker — adding one would require a schema migration across every
    /// <see cref="IDocketStore"/> backend (<c>DocketEntryEntity</c> + an EF migration shared by
    /// <c>SqliteDocketStore</c>/<c>PostgresDocketStore</c>), which is out of scope for this change per
    /// the P1 ruling. Today the only durable signal that a broadcast failure happened is the log line
    /// and the OTel event — the entry itself is indistinguishable in the store from one whose Evidence
    /// Card broadcast succeeded on the first try. Area 5 (store reconciliation) owns closing this gap;
    /// until then, an entry that never gets a reviewer decision still expires normally via
    /// <c>DocketExpiryService</c>, so it is not permanently stuck — just silently undiscoverable by
    /// push notification alone.
    /// </para>
    /// </summary>
    private async Task BroadcastEvidenceCardWithRetryAsync(
        string sessionId,
        Guid entryId,
        string toolName,
        EvidenceCardRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await transport.BroadcastToGroupAsync(
                sessionId, TransportEvent.EvidenceCardRequest, request, cancellationToken);
            logger.LogInformation(
                "Sent EvidenceCardRequest to group {SessionId} for DocketEntry {EntryId}",
                sessionId, entryId);
            return;
        }
        catch (Exception firstEx) when (firstEx is not OperationCanceledException)
        {
            logger.LogWarning(firstEx,
                "ReviewGate: Evidence Card broadcast failed for DocketEntry {EntryId}; retrying once",
                entryId);
        }

        try
        {
            await transport.BroadcastToGroupAsync(
                sessionId, TransportEvent.EvidenceCardRequest, request, cancellationToken);
            logger.LogInformation(
                "ReviewGate: Evidence Card broadcast succeeded on retry for DocketEntry {EntryId}",
                entryId);
        }
        catch (Exception secondEx) when (secondEx is not OperationCanceledException)
        {
            // DocketEntry {entryId} is durably Pending — do NOT rethrow. See method remarks: the
            // caller (FileForReviewCoreAsync) must still report RequiresReview/success.
            logger.LogError(secondEx,
                "ReviewGate: Evidence Card broadcast failed twice for DocketEntry {EntryId} — entry " +
                "is filed and Pending, but no reviewer has been notified via push",
                entryId);

            RecordBroadcastFailedEvent(entryId, secondEx);

            await NotifyBroadcastFailedBestEffortAsync(sessionId, entryId, toolName, cancellationToken);
        }
    }

    private static void RecordBroadcastFailedEvent(Guid entryId, Exception ex)
    {
        var target = AffiantTelemetry.FindAffiantActivity() ?? Activity.Current;
        target?.AddEvent(new ActivityEvent("affiant.review.broadcast_failed",
            tags: new ActivityTagsCollection
            {
                { "docket.entry_id", entryId.ToString() },
                { "exception.type", ex.GetType().Name }
            }));
    }

    /// <summary>
    /// Best-effort SystemNotification after both Evidence Card broadcast attempts fail. Guarded so a
    /// failure here (including cancellation) never escapes — this is pure observability, not part of
    /// the filing contract.
    /// </summary>
    private async Task NotifyBroadcastFailedBestEffortAsync(
        string sessionId, Guid entryId, string toolName, CancellationToken cancellationToken)
    {
        try
        {
            await transport.BroadcastToGroupAsync(
                sessionId,
                TransportEvent.SystemNotification,
                new SystemNotificationPayload(
                    "warning",
                    $"Your request to {toolName} was filed for review, but reviewers were " +
                    "not notified. It may need manual follow-up."),
                cancellationToken);
        }
        catch (Exception notifyEx)
        {
            logger.LogWarning(notifyEx,
                "ReviewGate: best-effort SystemNotification broadcast failed after DocketEntry " +
                "{EntryId}'s Evidence Card broadcast failed twice",
                entryId);
        }
    }

    // ── The decision surface (AZ-1, AZ-2, AZ-3, AZ-5, AZ-6, AZ-7) ─────────────────────────────
    //
    // Three entry points move a filed row: a decision, the host's execution report, and a
    // resubmission. Each takes a DecisionContext and each runs the SAME four checks against it,
    // in the same order, before it touches anything:
    //
    //   1. No resolved principal → refused, BEFORE the store is read. "Identity unknown" is never
    //      "allow", and a read that happened before the refusal is a read an attacker can time.
    //   2. The row is read inside the caller's tenant AND the row's own tenant is compared here.
    //      A scoped read alone is a check the STORE performs; a host store with a scope bug then
    //      fails open and the gate does not notice. Two independent enforcements of one rule is
    //      what "fail closed" is worth. A row in another tenant is entry-not-found, exactly as an
    //      id that never existed — never "not authorized", which would confirm the id exists.
    //   3. The host's own authorization port. False refuses; a throw refuses. A callback that fell
    //      over has not said yes.
    //   4. Only then the state machine's own checks — blocked, not pending, expired, lost race.
    //
    // None of this is conditional on a model, a transport or any other port being available: there
    // is no degraded path that skips a check (AZ-6).
    //
    // There is no `execute` here and no executor port on this class. The only path to
    // execution: executed is MarkExecutedAsync — the host's own executor saying what it did
    // against a row that already carries an attestation (AZ-5, AZ-7).

    /// <summary>
    /// Approve, amend or reject the entry <paramref name="entryId"/> names, as
    /// <c>context.Principal</c> (DK-1, AZ-1, AZ-2, AZ-3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// If a <see cref="FileReviewAsync"/> (or <see cref="FileForReviewAsync"/>) task is currently
    /// awaiting a response for <paramref name="entryId"/>, the decision is delivered directly and
    /// this method returns <c>(null, null)</c> — the awaiting caller owns the outcome and its
    /// completion. If no waiter exists (the host restarted, or the review was filed through the
    /// non-blocking path and never awaited), the decision is replayed through the Docket under a
    /// guarded compare-and-set.
    /// </para>
    /// <para>
    /// <b>The refusals are distinct answers, not one.</b> An unresolved principal, a row in another
    /// tenant, a principal the host declined, a blocked entry, an entry that is no longer pending, a
    /// decision that lost a race and a decision that arrived after the deadline are seven different
    /// things, and each is reported as its own <see cref="ReviewOutcome.Refused"/> code. A late
    /// decision from a principal who <em>could</em> have decided also has its amendments preserved on
    /// the row for a resubmission; one from a principal who could not leaves nothing behind, because
    /// a resubmission prefills preserved values as a person's own correction and a machine may not
    /// put words in a person's mouth.
    /// </para>
    /// <para>
    /// <b>Who is written onto the row.</b> The attestation is built from the principal and
    /// <em>only</em> from the principal: there is no parameter through which a caller can name whose
    /// signature this is. A member principal attests <c>member</c>; a service principal that names
    /// both the person it speaks for and the relay that carried them attests
    /// <c>member-via-relay</c>; a service principal with nothing to relay is refused with
    /// <c>decision-unauthorized</c> — a machine cannot agree to a write in a person's name (AZ-3).
    /// </para>
    /// </remarks>
    /// <param name="entryId">The entry being decided.</param>
    /// <param name="decision">Approve or reject.</param>
    /// <param name="context">Who is deciding, in which tenant, on which channel, and why.</param>
    /// <param name="amendments">
    /// Fields the reviewer changed while acting on the Evidence Card. Ignored on rejection. On an
    /// approval they are recorded as what the approval <em>accepted</em>, and the accepted state they
    /// produce is written beside the proposal (AF-4, PV-2). Carried by a refused late decision, they
    /// are preserved on the row as a different fact from what an approval accepted.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    public async Task<(ReviewOutcome? Outcome, DateTimeOffset? EntryCreatedAt)> HandleDecisionAsync(
        Guid entryId,
        ApprovalDecision decision,
        DecisionContext context,
        IReadOnlyDictionary<string, object?>? amendments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // (i) AZ-2, fail closed — before any store call at all.
        if (RequirePrincipal(context, entryId, DecidePath) is not { } principal)
            return (Unauthorized(entryId, AuthorizationRule), null);

        // (v) Who is being held to this (AZ-1, AZ-3). Checked here, before the live-waiter path and
        // before the store, because a caller that can attest nothing must not be able to complete a
        // review through the awaiting-caller shortcut either.
        if (Attestor.For(principal) is not { } attestor)
        {
            AffiantTelemetry.RecordDecisionUnauthorized(
                entryId, context.ConversationId, MachineAttestationReason, DecidePath, principal.Kind);
            logger.LogWarning(
                "HandleDecisionAsync: DocketEntry {EntryId} was decided by a machine caller with " +
                "nothing to relay — a service principal decides only on behalf of a person it names, " +
                "over a relay it names, and the result attests member-via-relay",
                entryId);
            return (Unauthorized(entryId, AttestationRule), null);
        }

        // (ii) The tenant is the boundary, and a miss is a miss (AZ-2).
        var entry = await RequireEntryAsync(entryId, context, DecidePath, cancellationToken);
        if (entry is null)
            return (NotFound(entryId), null);

        // (iii) The host's own answer.
        if (!await IsAuthorizedAsync(principal, entry, context, DecidePath, cancellationToken))
            return (Unauthorized(entryId, AuthorizationRule), entry.CreatedAt);

        var scope = new DocketScope(entry.TenantId);
        var createdAt = entry.CreatedAt;
        var now = _time.GetUtcNow();
        // AZ-1: when the decision was made is the gate's own observation, not the caller's claim.
        var decidedAt = now;

        // (iv) A blocked entry refuses every decision, and says which code blocked it. Checked before
        // the store so the refusal carries the marker's own context, which a bare transition result
        // cannot (AZ-4).
        if (entry.Blocked is not null)
        {
            AffiantTelemetry.RecordDecisionUnauthorized(
                entryId, context.ConversationId, DocketRefusalCodes.DecisionNotPending, DecidePath,
                principal.Kind);
            return (RefuseBlocked(entryId, entry.Blocked), createdAt);
        }

        var accepted = decision == ApprovalDecision.Approved && amendments is { Count: > 0 }
            ? amendments
            : null;

        var patch = new DocketTransitionPatch(
            Status: decision == ApprovalDecision.Approved ? ReviewStatus.Approved : ReviewStatus.Rejected,
            Decision: new DecisionRecord(
                decision == ApprovalDecision.Approved ? DecisionKind.Approve : DecisionKind.Reject,
                context.Reason,
                decidedAt),
            // What an approval ACCEPTS. A rejection accepts nothing, so it records nothing here —
            // a refused or rejected caller's edits are a different fact from an approval's.
            Amendments: accepted,
            // The accepted state those amendments produce — the Affidavit recomputed with the
            // reviewer's values, their act on each amended field's provenance chain, and all three
            // confidence numbers recomputed (AF-4, PV-2). Written BESIDE the proposal, never over it.
            AmendedAffidavit: accepted is null
                ? null
                : FoldAmendments(entry.Envelope, accepted, entryId, attestor.Subject),
            // Built from the principal and only from the principal (AZ-1).
            Attestation: new Attestation(attestor, decidedAt, entryId),
            DecidedAt: decidedAt);

        var result = await docketStore.TransitionAsync(
            entryId, scope, ReviewStatus.Pending, patch, cancellationToken);

        switch (result)
        {
            case DocketTransitionResult.Transitioned transitioned:
                logger.LogInformation(
                    "HandleDecisionAsync: DocketEntry {EntryId} {Decision}, attested {Attestation}",
                    entryId, decision, attestor.Kind);

                // TL-1 `docket.transition`, emitted by the caller whose own compare-and-set won it.
                AffiantTelemetry.RecordDocketTransition(
                    entryId,
                    entry.SessionId,
                    DocketStateName(ReviewStatus.Pending),
                    DocketStateName(transitioned.Entry.Status),
                    amended: transitioned.Entry.Amendments is { Count: > 0 },
                    execution: ExecutionStateName(transitioned.Entry.Execution),
                    decisionKind: decision == ApprovalDecision.Approved ? "approve" : "reject",
                    attestationKind: transitioned.Entry.Attestation?.By.Kind ?? attestor.Kind);

                var settled = transitioned.Entry.Status == ReviewStatus.Approved
                    ? (ReviewOutcome)new ReviewOutcome.Approved(entryId, transitioned.Entry.AmendedAffidavit)
                    : new ReviewOutcome.Rejected(entryId, context.Reason ?? "No reason provided");

                // A blocking FileReviewAsync may be holding this row open. It is unblocked by the
                // RESULT of the sequence above and performs no part of it: the row is already
                // written, attested, and reported here (AZ-1, AZ-2). A hand-off is the gate's to
                // mint, so nothing a host delivers can stand in for one.
                transport.TryDeliverResponse(
                    entryId,
                    new DecisionHandOff(
                        entryId,
                        decision,
                        transitioned.Entry.Attestation ?? new Attestation(attestor, decidedAt, entryId),
                        settled,
                        createdAt));

                return (settled, createdAt);

            case DocketTransitionResult.NotFound:
                AffiantTelemetry.RecordDecisionUnauthorized(
                    entryId, entry.SessionId, DocketRefusalCodes.EntryNotFound, DecidePath, principal.Kind);
                return (NotFound(entryId), createdAt);

            case DocketTransitionResult.Expired:
                return (await HandleLateDecisionAsync(
                    entryId, scope, context, principal, amendments, decidedAt, cancellationToken),
                    createdAt);

            case DocketTransitionResult.AlreadyDecided:
                // Which of the two "not pending" refusals this is depends on what the READ above saw.
                // If the row already looked decided then, this caller was late to the entry; if it
                // looked pending, this caller was late only to the race. Reporting both as one code
                // would tell a host that a user double-clicked when what happened was two reviewers
                // deciding at once, and those need different messages.
                var code = entry.Status == ReviewStatus.Pending
                    ? DocketRefusalCodes.DecisionLostRace
                    : DocketRefusalCodes.DecisionNotPending;
                logger.LogWarning(
                    "HandleDecisionAsync: DocketEntry {EntryId} refused with {Code}", entryId, code);
                AffiantTelemetry.RecordDecisionUnauthorized(
                    entryId, entry.SessionId, code, DecidePath, principal.Kind);
                return (new ReviewOutcome.Refused(
                        entryId,
                        code,
                        code == DocketRefusalCodes.DecisionLostRace
                            ? "Another decision on this entry won the race; the first one stands, and a " +
                              "decision is recorded once."
                            : $"DocketEntry {entryId} is no longer pending, and a decision is accepted " +
                              "only while it is."),
                    createdAt);

            default:
                throw new InvalidOperationException(
                    $"Unknown transition result {result.GetType().Name} for DocketEntry {entryId}.");
        }
    }

    /// <summary>
    /// Record what the host's executor did with an approved write — the only path to
    /// <see cref="ExecutionOutcome.Executed"/> (DK-1, AZ-5, AZ-7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The framework never performs the write.</b> There is no executor port on this class and no
    /// method here calls one: a host reads its approved-and-unexecuted rows
    /// (<see cref="DocketRehydration"/>), does the write itself against the attested row, and says
    /// what happened. An executor is reachable only through a Docket entry that carries an
    /// attestation — nothing replayed from a client's history, a chat transcript or a framework
    /// checkpoint stands in for that row, and a host's outbox is a retry of an already-attested
    /// write, never a second authorization path.
    /// </para>
    /// <para>
    /// <b><see cref="ReviewStatus.Approved"/> stays.</b> The approval happened and is not undone by a
    /// failed write; only the execution outcome and its detail move. An approved-but-failed write and
    /// an approved-and-committed one must stay distinguishable on the row.
    /// </para>
    /// <para>
    /// <b>Reported once.</b> The execution transition is a guarded compare-and-set out of
    /// <see cref="ExecutionOutcome.Unexecuted"/>, like every other transition on a row: a second
    /// report is refused with <c>execution-already-recorded</c> and the first stands. Overwriting
    /// would let an approved-and-committed row later read failed — an edit in place of a recorded
    /// fact, and the loss of exactly the distinction the row exists to keep. A host that retries a
    /// write reports <b>once</b>, when it knows the outcome: the retries are the host's business, the
    /// outcome is the Docket's.
    /// </para>
    /// <para>
    /// <b>A machine caller is admitted here and refused as a decider</b> (AZ-3). The asymmetry is the
    /// point: reporting an outcome is a statement of fact about work the host performed, which a
    /// machine is the right party to make, while a decision is an act of authority a machine may
    /// never make in a person's name. The tenant check and the host's authorization port still apply,
    /// so "which service may report on this entry" is still the host's answer and not an open door.
    /// </para>
    /// </remarks>
    /// <param name="entryId">The approved entry the executor acted on.</param>
    /// <param name="outcome">
    /// <see cref="ExecutionOutcome.Executed"/> or <see cref="ExecutionOutcome.Failed"/>.
    /// <see cref="ExecutionOutcome.Unexecuted"/> is the state a row is <em>filed</em> in, never a
    /// report — an executor with nothing to say says nothing.
    /// </param>
    /// <param name="detail">What the executor reported — an id, an error — or <c>null</c>.</param>
    /// <param name="context">Who is reporting, in which tenant.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is <see cref="ExecutionOutcome.Unexecuted"/>.
    /// </exception>
    public async Task<ReviewOutcome> MarkExecutedAsync(
        Guid entryId,
        ExecutionOutcome outcome,
        string? detail,
        DecisionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (outcome == ExecutionOutcome.Unexecuted)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                "AZ-5: `unexecuted` is the state an approved row is filed in, not an outcome an " +
                "executor reports. Report `executed` or `failed`, once, when the write's fate is known.");
        }

        if (RequirePrincipal(context, entryId, MarkExecutedPath) is not { } principal)
            return Unauthorized(entryId, AuthorizationRule);

        var entry = await RequireEntryAsync(entryId, context, MarkExecutedPath, cancellationToken);
        if (entry is null)
            return NotFound(entryId);

        if (!await IsAuthorizedAsync(principal, entry, context, MarkExecutedPath, cancellationToken))
            return Unauthorized(entryId, AuthorizationRule);

        // AZ-5: an executor is reachable only through a Docket entry that CARRIES AN ATTESTATION.
        // The row is read before the report is written, so a row approved with nobody on it — a
        // state the decision core makes unreachable, and which no store member will write or
        // execute against — is refused here too, which is where a host learns why. A row that is
        // simply not approved is a different
        // answer, `decision-not-pending`, which the store's own guard produces below: there is no
        // authorised write for an executor to have performed, and that is not an authorization
        // failure to report to an operator.
        if (entry.Status == ReviewStatus.Approved && entry.Attestation is null)
        {
            AffiantTelemetry.RecordDecisionUnauthorized(
                entryId, entry.SessionId, NotAuthorizedReason, MarkExecutedPath, principal.Kind);
            logger.LogWarning(
                "MarkExecutedAsync: DocketEntry {EntryId} is approved and carries no attestation; an " +
                "executor is reachable only through an entry that says who approved it",
                entryId);
            return Unauthorized(entryId, "AZ-5");
        }

        var scope = new DocketScope(entry.TenantId);
        var result = await docketStore.RecordExecutionAsync(
            entryId, scope, outcome, detail, ExecutionOutcome.Unexecuted, cancellationToken);

        switch (result)
        {
            case RecordExecutionResult.Recorded recorded:
                logger.LogInformation(
                    "MarkExecutedAsync: DocketEntry {EntryId} reported {Outcome}", entryId, outcome);
                AffiantTelemetry.RecordDocketTransition(
                    entryId,
                    entry.SessionId,
                    DocketStateName(ReviewStatus.Approved),
                    DocketStateName(recorded.Entry.Status),
                    amended: recorded.Entry.Amendments is { Count: > 0 },
                    execution: ExecutionStateName(recorded.Entry.Execution),
                    attestationKind: recorded.Entry.Attestation?.By.Kind);
                return new ReviewOutcome.Approved(entryId, recorded.Entry.AmendedAffidavit);

            case RecordExecutionResult.NotFound:
                AffiantTelemetry.RecordDecisionUnauthorized(
                    entryId, entry.SessionId, DocketRefusalCodes.EntryNotFound, MarkExecutedPath,
                    principal.Kind);
                return NotFound(entryId);

            case RecordExecutionResult.NotApproved:
                logger.LogWarning(
                    "MarkExecutedAsync: DocketEntry {EntryId} is {Status}, so there is no authorised " +
                    "write for an executor to have performed",
                    entryId, entry.Status);
                return new ReviewOutcome.Refused(
                    entryId,
                    DocketRefusalCodes.DecisionNotPending,
                    $"DocketEntry {entryId} is {entry.Status}, so there is no authorised write for an " +
                    "executor to have performed.");

            case RecordExecutionResult.ExecutionAlreadyRecorded:
                logger.LogWarning(
                    "MarkExecutedAsync: DocketEntry {EntryId} already carries an execution outcome; " +
                    "this report is refused rather than written over it",
                    entryId);
                return new ReviewOutcome.Refused(
                    entryId,
                    DocketRefusalCodes.ExecutionAlreadyRecorded,
                    ExecutionReportsOnce);

            default:
                throw new InvalidOperationException(
                    $"Unknown execution result {result.GetType().Name} for DocketEntry {entryId}.");
        }
    }

    /// <summary>
    /// The decision arrived after the deadline. Persist the expiry the row had already earned, tell
    /// the session group, and keep the reviewer's corrections for a resubmission.
    /// </summary>
    /// <remarks>
    /// The deadline is inclusive — a decision landing exactly on it is late — and expiry is a state a
    /// read already applies, so this path runs whether or not the sweep has reached the row. What the
    /// sweep would have added is the durable transition and the broadcast, which is what this does:
    /// leaving the row pending-with-a-lapsed-deadline for up to a sweep interval starved the
    /// resubmission guard for that whole window.
    /// <para>
    /// The amendments are preserved with the decision's <b>own</b> instant and principal, not the
    /// store's clock and not the row's deadline: a resubmission prefills them as that person's
    /// correction, and dating them to the sweep would place the correction at a moment nobody typed
    /// anything. Preserved only for a principal who could have decided — the caller has already been
    /// through the attestation check, so what is written names a person.
    /// </para>
    /// </remarks>
    private async Task<ReviewOutcome> HandleLateDecisionAsync(
        Guid entryId,
        DocketScope scope,
        DecisionContext context,
        Principal principal,
        IReadOnlyDictionary<string, object?>? amendments,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "HandleDecisionAsync: DocketEntry {EntryId} TTL lapsed before this decision arrived", entryId);

        AffiantTelemetry.RecordDecisionUnauthorized(
            entryId, context.ConversationId, DocketRefusalCodes.DecisionExpired, DecidePath, principal.Kind);

        var preserved = false;
        if (amendments is { Count: > 0 } && Attestor.For(principal) is { } attestor)
        {
            var outcome = await docketStore.PreserveAmendmentsAsync(
                entryId, scope, amendments, new PreservedAct(decidedAt, attestor.Subject), cancellationToken);
            preserved = outcome is PreserveAmendmentsResult.Preserved;
            if (preserved)
            {
                logger.LogInformation(
                    "HandleDecisionAsync: preserved late amendments onto expired DocketEntry {EntryId}", entryId);
            }
        }

        // Persist the expiry the row already reads as, guarded, and broadcast only if this call's own
        // write is the one that transitioned it — a repeat late decision affects no row and must not
        // re-notify.
        var expiry = await docketStore.TransitionAsync(
            entryId,
            scope,
            ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Expired),
            cancellationToken);

        if (expiry is DocketTransitionResult.Transitioned)
        {
            await DocketExpiryBroadcaster.VerifyAndBroadcastIfExpiredAsync(
                docketStore, transport, entryId, cancellationToken);
        }

        return new ReviewOutcome.Refused(
            entryId,
            DocketRefusalCodes.DecisionExpired,
            preserved ? "amendments-preserved" : null);
    }

    // ── The four checks, in the order every entry point runs them (AZ-2) ──────────────────────

    /// <summary>
    /// The principal on <paramref name="context"/>, or <c>null</c> having already emitted the
    /// refusal. Called before every store access on every entry point here.
    /// </summary>
    /// <remarks>
    /// A <c>null</c> principal means the host has not resolved an identity, which is not the same as
    /// anonymous and is never treated as permission. The refusal happens before the Docket is read:
    /// the rule is not only about the answer, it is about not doing any work on an entry for a caller
    /// who has not been identified.
    /// </remarks>
    private Principal? RequirePrincipal(DecisionContext context, Guid entryId, string path)
    {
        if (context.Principal is { } principal) return principal;

        AffiantTelemetry.RecordDecisionUnauthorized(
            entryId, context.ConversationId, IdentityUnresolvedReason, path, "unresolved");
        logger.LogWarning(
            "{Path}: DocketEntry {EntryId} was acted on with no resolved principal — the gate fails " +
            "closed here, before it reads the Docket",
            path, entryId);
        return null;
    }

    /// <summary>
    /// The entry inside the caller's tenant, or <c>null</c> having already emitted the refusal —
    /// never an oracle (AZ-2).
    /// </summary>
    /// <remarks>
    /// The row's own tenant is compared here, after the read, whatever the store returned. A check
    /// that consists solely of passing a tenant id to the store is a check the <em>store</em>
    /// performs, and a host store with a scope bug then fails open with the gate none the wiser. The
    /// caller is told only that there is no such entry, which is also the answer a caller in another
    /// tenant gets; the host's telemetry separates the two reasons, because a scoped read that
    /// answered with another tenant's row is a bug in that store and the one thing this second check
    /// exists to surface.
    /// </remarks>
    private async Task<DocketEntry?> RequireEntryAsync(
        Guid entryId, DecisionContext context, string path, CancellationToken cancellationToken)
    {
        var entry = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
        if (entry is not null && string.Equals(entry.TenantId, context.TenantId, StringComparison.Ordinal))
            return entry;

        AffiantTelemetry.RecordDecisionUnauthorized(
            entryId,
            context.ConversationId,
            entry is null ? DocketRefusalCodes.EntryNotFound : TenantMismatchReason,
            path,
            context.Principal?.Kind ?? "unresolved");

        if (entry is not null)
        {
            logger.LogWarning(
                "{Path}: DocketEntry {EntryId} is outside the caller's tenant; reported as not found",
                path, entryId);
        }

        return null;
    }

    /// <summary>The host's own answer to "may this principal act on this entry" (AZ-2).</summary>
    /// <remarks>
    /// A port that throws is a refusal, never an approval: an authorization callback that fell over
    /// has not said yes. Cancellation is not a fault and propagates.
    /// </remarks>
    private async Task<bool> IsAuthorizedAsync(
        Principal principal,
        DocketEntry entry,
        DecisionContext context,
        string path,
        CancellationToken cancellationToken)
    {
        bool admitted;
        try
        {
            admitted = await authorization.MayDecideAsync(principal, entry, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "{Path}: the host's IDecisionAuthorizationPolicy threw for DocketEntry {EntryId}; a " +
                "port that throws is a refusal, never an approval",
                path, entry.EntryId);
            admitted = false;
        }

        if (admitted) return true;

        AffiantTelemetry.RecordDecisionUnauthorized(
            entry.EntryId, entry.SessionId, NotAuthorizedReason, path, principal.Kind);
        logger.LogWarning(
            "{Path}: the host's authorization port did not admit this principal for DocketEntry {EntryId}",
            path, entry.EntryId);
        return false;
    }

    /// <summary>
    /// The one refusal every authorization failure answers with, naming the rule that refused and
    /// not the fact that tripped it.
    /// </summary>
    /// <remarks>
    /// A caller learns <em>that</em> it may not act, and which rule says so — never which check
    /// inside that rule fired, and never anything about the row. The host's own
    /// <c>decision.unauthorized</c> event carries the four reasons separately; a caller and an
    /// operator are different audiences.
    /// </remarks>
    private static ReviewOutcome.Refused Unauthorized(Guid entryId, string rule) =>
        new(entryId, DocketRefusalCodes.DecisionUnauthorized, rule);

    /// <summary>
    /// The entry id a proposal derives to: a name-based UUID over the tenant, the conversation, the
    /// tool and the canonical form of the operation and its arguments (GT-4). See
    /// <see cref="EntryIdDerivation"/> for the material and the layout, which are the protocol's and
    /// not this implementation's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived rather than invented so that the same proposal, re-filed in the same conversation
    /// after a retry or a reconnect, replays to the row it already has instead of filing a second
    /// one — and so that two tenants cannot land on the same id by accident, which is what lets the
    /// replay lookup treat a row outside the caller's tenant as a miss.
    /// </para>
    /// <para>
    /// A host that wants its own id still passes <see cref="ReviewContext.EntryId"/>.
    /// </para>
    /// </remarks>
    private static Guid DeriveEntryId(ReviewContext context, WriteProposal proposal) =>
        EntryIdDerivation.Derive(
            context.TenantId,
            context.SessionId,
            proposal.ToolName,
            context.Affidavit,
            proposal.Arguments,
            context.Supersedes);

    /// <summary>The rule that refuses an unresolved principal, a wrong tenant or a declining host port.</summary>
    private const string AuthorizationRule = "AZ-2";

    /// <summary>
    /// Why a second execution report is refused, on the refusal itself and not only in a log line
    /// (AZ-5). A caller learns what happened from what it is handed back.
    /// </summary>
    private const string ExecutionReportsOnce =
        "This entry already carries an execution outcome. A host reports once, when the write's fate " +
        "is known: overwriting would let an approved-and-committed row later read failed.";

    /// <summary>The rule that refuses a machine caller trying to attest a decision.</summary>
    private const string AttestationRule = "AZ-3";

    /// <summary>The `reason` attribute on a `decision.unauthorized` event: no principal was resolved.</summary>
    private const string IdentityUnresolvedReason = "identity-unresolved";

    /// <summary>…the host's port declined, or threw.</summary>
    private const string NotAuthorizedReason = "not-authorized";

    /// <summary>…a machine caller with nothing to relay tried to attest a decision.</summary>
    private const string MachineAttestationReason = "machine-attestation";

    /// <summary>
    /// …a scoped read answered with a row from another tenant. Distinguished from
    /// <c>entry-not-found</c> for the host alone: the caller gets one answer, the operator gets the
    /// one that says a store has a scope bug.
    /// </summary>
    private const string TenantMismatchReason = "tenant-mismatch";


    /// <summary>The refusal a blocked entry answers every act with, carrying the marker's own context.</summary>
    /// <remarks>
    /// <b>AZ-4.</b> The code is <c>decision-not-pending</c> — the row is pending and no decision on
    /// it will ever be accepted, which is exactly what that code is registered to mean — and the
    /// marker's own code and context travel in the detail. Answering with the marker's code
    /// <em>as</em> the refusal code would tell a caller that its act failed validation, when what
    /// happened is that this row does not accept decisions at all.
    /// </remarks>
    private static ReviewOutcome.Refused RefuseBlocked(Guid entryId, BlockedMarker marker) =>
        new(entryId, DocketRefusalCodes.DecisionNotPending, BlockedDetail(marker));

    /// <summary>The blocked marker's code, and the context that code makes meaningful.</summary>
    private static string BlockedDetail(BlockedMarker marker) => marker switch
    {
        BlockedMarker.RequirementNotImplemented r => $"{r.Code}: {r.Level}",
        BlockedMarker.CoverageRefused c => $"{c.Code}: {c.ToolName}",
        _ => marker.Code,
    };

    /// <summary>
    /// The refusal a row the caller may not see answers with, and the reason it gives.
    /// </summary>
    /// <remarks>
    /// A row in another tenant and a row that does not exist are the same answer on purpose (AZ-2):
    /// telling a caller that an id it may not touch exists is the leak the check is for. The detail
    /// says which act was refused and nothing about the row.
    /// </remarks>
    private static ReviewOutcome.Refused NotFound(Guid entryId) =>
        new(entryId, DocketRefusalCodes.EntryNotFound,
            $"No Docket entry {entryId} is visible to this caller.");

    /// <summary>
    /// Folds an accepted amendment into the filed proposal, returning the amended
    /// <see cref="Affidavit"/> that sits beside it — the reviewer's corrections as the values, their
    /// act on top of each amended field's chain, and all three confidence numbers recomputed, so a
    /// corrected card never reports the machine's pre-correction confidence.
    /// </summary>
    /// <remarks>
    /// This never affects whether the decision stuck: a map naming a field the Affidavit does not
    /// propose is a host defect (a surface that offered an edit for a field the write never
    /// proposed), and the right answer is a loud warning and no amended record — not an exception
    /// thrown while the transition patch is being built, which would lose the decision rather than
    /// the extra key.
    /// </remarks>
    private Affidavit? FoldAmendments(
        Affidavit proposal,
        IReadOnlyDictionary<string, object?> amendments,
        Guid entryId,
        string reviewerId)
    {
        try
        {
            return AffidavitAmendments.Apply(
                proposal, amendments, entryId, _time.GetUtcNow(), reviewerId);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex,
                "ReviewGate: the amendments accepted on DocketEntry {EntryId} name a field the filed " +
                "Affidavit does not propose, so no amended Affidavit was produced. The amendments " +
                "themselves are persisted on the entry; the decision stands.",
                entryId);
            return null;
        }
    }

    /// <summary>
    /// Refuses a proposal that swears to nothing, before anything is filed and before any policy
    /// runs (protocol rule GT-3): every proposed field reads <c>Empty</c>, there are no fields at
    /// all, or a field asserts a value while its provenance reads <c>Empty</c> — the hollow
    /// signature. Nothing is filed, nothing is counted, nothing is broadcast, and the caller's tool
    /// result becomes the error arm carrying <c>substance-refused</c>.
    ///
    /// <para>
    /// <b>Why at run time and not only in a test harness.</b> The founding incident this rule exists
    /// for is a system whose structural tests were entirely green — right shape, right field names,
    /// right envelope — while every Affidavit it produced swore to nothing, so a proposal that knew
    /// nothing reached a reviewer looking exactly like one that knew everything. Until
    /// <c>1.0.0-beta.1</c> the framework's answer was
    /// <c>ComplianceHarness.AssertProvenanceIsSubstantive</c>, which runs in an adopter's own test
    /// suite and never in production, plus a telemetry event at the projection that reported the
    /// case and carried on. A rule that only holds where someone wrote a test is not a rule the gate
    /// enforces. The harness keeps its check — a host wants the failure at build time too — and the
    /// gate now refuses at run time as well.
    /// </para>
    ///
    /// <para>
    /// <b>What counts as empty.</b> <see langword="null"/> and a blank string. <c>0</c>,
    /// <see langword="false"/>, an empty array and an empty object are values a field can honestly
    /// swear to, and a proposal that says "the count is zero" is a proposal, not a hollow one.
    /// </para>
    /// </summary>
    private void RefuseProposalWithoutSubstance(WriteProposal proposal, ReviewContext context)
    {
        var failure = AffidavitSubstance.DescribeFailure(context.Affidavit);
        if (failure is null) return;

        // TL-1 `affidavit.refused.substance` (GT-3). The reason names field NAMES, which are schema,
        // and never a field value.
        AffiantTelemetry.RecordSubstanceRefused(
            proposal.ToolName,
            context.SessionId,
            context.Affidavit.Fields.Length,
            failure);

        logger.LogWarning(
            "ReviewGate refused the proposal from {ToolName}: {Reason}. Nothing was filed and no " +
            "reviewer was asked.",
            proposal.ToolName, failure);

        throw new AffiantSubstanceException(
            $"GT-3: the write '{proposal.ToolName}' proposed swears to nothing — {failure}. It was " +
            "not filed, not counted and not broadcast: a reviewer confirming a proposal that knows " +
            "nothing is the incident this gate exists to prevent, not an edge case it tolerates. " +
            "Check that the tool's Affidavit projection is filling the fields it declares.");
    }

    // ── The telemetry-key registry (TL-1) ────────────────────────────────────────────────────

    /// <summary>
    /// The three <c>path</c> attribute values a <c>decision.unauthorized</c> event carries — one per
    /// entry point on the decision surface, so an operator can tell a refused decision from a refused
    /// execution report from a refused resubmission without reading a stack trace.
    /// </summary>
    private const string DecidePath = "decide";

    /// <inheritdoc cref="DecidePath"/>
    private const string MarkExecutedPath = "mark-executed";

    /// <inheritdoc cref="DecidePath"/>
    private const string ResubmitPath = "resubmit";

    /// <summary>
    /// What a Standing Order attestation records when the chain could not name the policy that
    /// fired. Unreachable through the shipped evaluator, which stamps the policy's own type name on
    /// every verdict it returns; present because AZ-1 admits no attestation with a blank attributor,
    /// and a stated placeholder is a better record than an empty string.
    /// </summary>
    private const string UnnamedStandingOrderPolicy = "unnamed-standing-order";

    /// <summary>
    /// The rulebook's name for an execution outcome (<c>unexecuted</c>, <c>executed</c>,
    /// <c>failed</c>), or <c>null</c> for a row that has none — a rejected or expired one, which was
    /// never authorised and so has no write to have an outcome.
    /// </summary>
    private static string? ExecutionStateName(ExecutionOutcome? execution) => execution switch
    {
        null => null,
        ExecutionOutcome.Unexecuted => "unexecuted",
        ExecutionOutcome.Executed => "executed",
        ExecutionOutcome.Failed => "failed",
        _ => execution.Value.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Where this proposal came from, as the approval-policy chain is given it: the conversation, the
    /// person whose turn produced it, the tenant and the channel.
    /// </summary>
    /// <remarks>
    /// <c>StartedAt</c> is the conversation's own start when the host supplied one on the
    /// <see cref="ReviewContext"/> and this filing's instant otherwise — the gate does not know when
    /// a conversation began and will not guess an earlier time than the one it can vouch for.
    /// </remarks>
    private static ConversationIdentity IdentityOf(ReviewContext context, DateTimeOffset now) =>
        new(
            SessionId: context.SessionId,
            UserId: context.UserId,
            StartedAt: context.ConversationStartedAt ?? now,
            HostAppName: null,
            TenantId: context.TenantId,
            Channel: context.Channel);

    /// <summary>
    /// Emits <c>docket.transition</c> for a guarded write that affected a row, and nothing at all
    /// for one that did not. A caller whose compare-and-set lost the race did not transition the
    /// entry — the caller that won it reports the transition, exactly once, which is what makes a
    /// count of these events a count of state changes rather than of attempts.
    /// </summary>
    private static void RecordTransitionIfWon(
        int rowsAffected,
        Guid entryId,
        string? conversationId,
        ReviewStatus to,
        string? decisionKind = null,
        bool? amended = null)
    {
        if (rowsAffected == 0) return;

        // `from` is always `pending`: every store implementation guards the write with
        // `WHERE Status = 'Pending'` (IDocketStore.TransitionAsync's double-submit
        // contract), so a write that affected a row can only have come from pending.
        AffiantTelemetry.RecordDocketTransition(
            entryId,
            conversationId,
            DocketStateName(ReviewStatus.Pending),
            DocketStateName(to),
            amended: amended,
            decisionKind: decisionKind);
    }

    /// <summary>
    /// The rulebook's name for a review state (DK-1: <c>pending</c>, <c>approved</c>,
    /// <c>rejected</c>, <c>expired</c>). <see cref="ReviewStatus.Deferred"/> has no rulebook state —
    /// the referral transitions are reserved for protocol v0.2 — and is reported under its own name
    /// rather than folded into one of the four.
    /// </summary>
    private static string DocketStateName(ReviewStatus status) => status switch
    {
        ReviewStatus.Pending => "pending",
        ReviewStatus.Approved => "approved",
        ReviewStatus.Rejected => "rejected",
        ReviewStatus.Expired => "expired",
        ReviewStatus.Deferred => "deferred",
        _ => status.ToString().ToLowerInvariant(),
    };
}
