namespace Affiant.Core.Services;

using System.Diagnostics;
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
    TimeProvider? timeProvider = null)
{
    /// <summary>
    /// The gate's only clock. Every instant it stamps (<c>DocketEntry.CreatedAt</c>,
    /// <c>ExpiresAt</c>, a resubmission's <c>WriteProposal</c>) and every deadline comparison it
    /// makes reads from here, so a host or a test can move time without moving the machine's.
    /// Defaults to <see cref="TimeProvider.System"/> — <c>AddAffiantCore</c> registers exactly that
    /// as the DI default, so a host that does nothing sees no change.
    /// </summary>
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

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
    /// <b>Document-reserved (P1a, area-4 Decision-1 ruling 2026-08-04) — retired, not deleted.
    /// Structurally deadlocks over the framework's only shipped transport; not the production
    /// default.</b> File a review for the given <paramref name="proposal"/> and block until the
    /// final outcome is known. Delegates the filing + policy-evaluation work to
    /// <see cref="FileForReviewAsync"/> and adds only the blocking await for a reviewer decision.
    /// <para>
    /// <b>Why this deadlocks:</b> when policy requires a human reviewer, this method awaits
    /// <see cref="IStreamingTransport.AwaitEvidenceCardResponseAsync"/> on the SAME call chain the
    /// caller's own connection is holding open. Over SignalR — the framework's only shipped
    /// transport — <c>HubOptions.MaximumParallelInvocationsPerClient</c> defaults to <c>1</c> and is
    /// never overridden by either reference host, so the one hub invocation that could deliver the
    /// reviewer's decision (e.g. an <c>ApproveAction</c>/<c>RejectAction</c> RPC) queues behind the
    /// very invocation blocked here awaiting it — a same-connection deadlock proven live
    /// (host-apps#25, Jaeger-traced 610.7s block; the incident's own words: "live approval has
    /// plausibly never once succeeded through the browser UI"). Every call to this method that
    /// requires human review will wait out <see cref="AffiantCoreOptions.DefaultDocketTtl"/> and
    /// resolve as <see cref="ReviewOutcome.Expired"/> under that condition, not because the reviewer
    /// failed to act, but because their decision cannot physically reach this awaiting call.
    /// </para>
    /// <para>
    /// <b>What to use instead:</b> the production default is
    /// <see cref="Affiant.Core.Filters.ReviewGateFilter"/> calling the non-blocking
    /// <see cref="FileForReviewAsync"/> and ending the calling turn on
    /// <see cref="ReviewFilingResult.RequiresReview"/> (P5a) — the eventual decision arrives through
    /// a separate hub RPC routed to <c>HandleDecisionAsync</c>, never through this method's
    /// own await. This method remains callable — kept because the underlying design (a synchronous
    /// wait-for-external-event, mirroring the Azure Durable Functions <c>WaitForExternalEvent</c>
    /// pattern; framework spec §4) is legitimate and has a real future use (a caller that must not
    /// proceed to a dependent tool call until the write is confirmed) — but it needs the decision to
    /// travel on a channel other than the blocked connection to be sound. That redesign is tracked in
    /// affiant#29 (design ticket, no implementation planned yet); do not reach for this method in new
    /// code until it lands.
    /// </para>
    /// </summary>
    /// <param name="proposal">The proposed write operation awaiting review.</param>
    /// <param name="context">Session, tenant, user, and affidavit context for routing the review.</param>
    /// <param name="cancellationToken">Caller cancellation — distinct from the internal timeout.</param>
    /// <returns>
    /// <see cref="ReviewOutcome.Approved"/>, <see cref="ReviewOutcome.Rejected"/>,
    /// <see cref="ReviewOutcome.Expired"/>, or <see cref="ReviewOutcome.Referral"/>.
    /// </returns>
    public async Task<ReviewOutcome> FileReviewAsync(
        WriteProposal proposal,
        ReviewContext context,
        CancellationToken cancellationToken = default)
    {
        var filing = await FileForReviewAsync(proposal, context, cancellationToken);
        if (filing is ReviewFilingResult.Decided decided)
            return decided.Outcome;

        var entryId = ((ReviewFilingResult.RequiresReview)filing).EntryId;

        EvidenceCardResponse response;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(options.DefaultDocketTtl);
            response = await transport.AwaitEvidenceCardResponseAsync(
                context.SessionId, entryId, cts.Token);
            logger.LogInformation(
                "Received EvidenceCardResponse for DocketEntry {EntryId}: {Decision}",
                entryId, response.Decision);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout (internal CTS fired, not the caller).
            logger.LogWarning(
                "EvidenceCardRequest timed out for DocketEntry {EntryId} after {Minutes} minutes",
                entryId, options.DefaultDocketTtl.TotalMinutes);

            // Guarded update: 0 rows means a reviewer's decision (restart path, via
            // HandleDecisionAsync) already transitioned this entry a beat earlier. Only broadcast
            // DocketExpired — and only report Expired — when this call is the one that actually
            // performed the transition; otherwise report and leave untouched the status the entry
            // genuinely landed in, so we never lie to the session group about what happened.
            var expiryRowsAffected = await docketStore.UpdateReviewStatusAsync(
                entryId, ReviewStatus.Expired, cancellationToken);
            if (expiryRowsAffected == 0)
            {
                var finalEntry = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
                return finalEntry is null
                    ? new ReviewOutcome.Expired(entryId)
                    : finalEntry.Status.ToReviewOutcome(entryId);
            }

            RecordTransitionIfWon(expiryRowsAffected, entryId, context.SessionId, ReviewStatus.Expired);

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

        // Process the reviewer's decision.
        if (response.Decision == ApprovalDecision.Rejected)
        {
            var rejectedRows = await docketStore.UpdateReviewStatusAsync(
                entryId, ReviewStatus.Rejected, cancellationToken);
            RecordTransitionIfWon(
                rejectedRows, entryId, context.SessionId, ReviewStatus.Rejected,
                decisionKind: "reject");
            return new ReviewOutcome.Rejected(entryId, response.Reason ?? "No reason provided");
        }

        // Approved: optimistic update — 0 rows means the entry was already transitioned.
        var rowsAffected = await docketStore.UpdateReviewStatusAsync(
            entryId, ReviewStatus.Approved, cancellationToken);
        if (rowsAffected == 0)
        {
            var finalEntry = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
            return finalEntry is null
                ? new ReviewOutcome.Expired(entryId)
                : finalEntry.Status.ToReviewOutcome(entryId);
        }

        // This call won the approval race — persist the reviewer's amendments (if any) onto
        // the entry it just transitioned. See EvidenceCardResponse.Amendments.
        Affidavit? amendedAffidavit = null;
        var amended = response.Amendments is { Count: > 0 };
        if (amended)
        {
            await docketStore.UpdateAmendmentsAsync(entryId, response.Amendments!, cancellationToken);
#pragma warning disable AFFIANT0001 // the routing hint until the attestation names the reviewer
            amendedAffidavit = FoldAmendments(
                context.Affidavit, response.Amendments!, entryId, context.ReviewerUserId);
#pragma warning restore AFFIANT0001
        }

        RecordTransitionIfWon(
            rowsAffected, entryId, context.SessionId, ReviewStatus.Approved,
            decisionKind: "approve", amended: amended);

        return new ReviewOutcome.Approved(entryId, amendedAffidavit);
    }

    /// <summary>
    /// Resubmits an expired review for a fresh reviewer round (framework half of repo issue #9):
    /// files a brand-new Pending <see cref="DocketEntry"/> (new <see cref="DocketEntry.EntryId"/>,
    /// fresh TTL) cloning the expired entry's envelope/affidavit via <see cref="FileForReviewAsync"/>,
    /// and broadcasts its Evidence Card carrying the original entry's persisted
    /// <see cref="DocketEntry.Amendments"/> in <see cref="EvidenceCardRequest.PriorAmendments"/> so
    /// the reviewer sees what was already agreed before the window lapsed.
    /// </summary>
    /// <param name="expiredEntryId">The <see cref="DocketEntry.EntryId"/> of the expired entry to resubmit.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>The <see cref="ReviewFilingResult"/> for the fresh entry — see <see cref="FileForReviewAsync"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="expiredEntryId"/> does not identify an existing entry, the entry's
    /// <see cref="DocketEntry.Status"/> is not <see cref="ReviewStatus.Expired"/>, or a concurrent
    /// <see cref="ResubmitAsync"/> call already claimed it (see remarks). An entry whose deadline
    /// has passed reads as <see cref="ReviewStatus.Expired"/> whether or not the sweep has reached
    /// it, so a resubmission never has to wait for a sweep tick.
    /// </exception>
    /// <remarks>
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
        CancellationToken cancellationToken = default)
    {
        var entry = await docketStore.GetDocketEntryAsync(expiredEntryId, cancellationToken);
        if (entry is null)
        {
            throw new InvalidOperationException(
                $"ResubmitAsync: DocketEntry {expiredEntryId} was not found.");
        }

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
        var prefill = entry.PreservedAmendments?.Amendments ?? entry.Amendments;
        var priorAmendments = prefill is { Count: > 0 } ? prefill : null;

        var proposal = new WriteProposal(entry.ToolName, _time.GetUtcNow(), entry.Envelope);
        var context = new ReviewContext(
            SessionId: entry.SessionId,
            TenantId: entry.TenantId,
            UserId: entry.UserId,
            // DocketEntry.ReviewerUserId is null for self-reviewed entries (see DocketEntry
            // remarks); ReviewContext requires a non-null reviewer, so self-review falls back
            // to the original proposer.
#pragma warning disable AFFIANT0001 // superseded by the attestation; still the routing hint until then
            ReviewerUserId: entry.ReviewerUserId ?? entry.UserId,
#pragma warning restore AFFIANT0001
            Affidavit: entry.Envelope,
            EntryId: newEntryId,
            Amendments: priorAmendments,
            // The other half of the lineage. The successor link was written on the superseded row
            // above; this is what the new row records about where it came from, so the history reads
            // forward from either end without a reverse lookup.
            Supersedes: expiredEntryId);

        ReviewFilingResult filing;
        try
        {
            filing = await FileForReviewCoreAsync(proposal, context, cancellationToken);
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
            expiredEntryId, newEntryId, filing.GetType().Name);

        return filing;
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

        var entryId = context.EntryId ?? Guid.NewGuid();

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
                    docketStore, entryId, existing.Envelope, existing.ExpiresAt, cancellationToken);
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
            var verdict = await evaluator.EvaluateAsync(context.Affidavit, cancellationToken);
            var requirement = verdict.Requirement;

            // 3. The deadline, stamped from the policy result and only now (GT-4): the verdict's own
            //    window, else the policy's declared default (the chain already folded that in), else
            //    this gate's. One global default applied before the policy chain is what the rule
            //    calls non-conformant, and it is what this method used to do. Stamped from the same
            //    instant CreatedAt is, so a pinned clock sees the window it asked for.
            var expiresAt = now.Add(verdict.TimeToLive ?? options.DefaultDocketTtl);

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
                Envelope: context.Affidavit,
                Status: ReviewStatus.Pending,
                CreatedAt: now,
                ExpiresAt: expiresAt,
                Amendments: amendments,
                Supersedes: context.Supersedes,
                ProtocolVersion: AffiantProtocol.Version)
            {
                ToolName = proposal.ToolName
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

            // 5a. StandingOrder: auto-approve without client interaction.
            if (requirement == ReviewRequirement.StandingOrder)
            {
                var approvedRows = await docketStore.UpdateReviewStatusAsync(
                    entryId, ReviewStatus.Approved, cancellationToken);
                RecordTransitionIfWon(approvedRows, entryId, context.SessionId, ReviewStatus.Approved);
                logger.LogInformation("StandingOrder auto-approved DocketEntry {EntryId}", entryId);
                return new ReviewFilingResult.Decided(new ReviewOutcome.Approved(entryId));
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
                await docketStore.MarkBlockedAsync(entryId, blocked, cancellationToken);
                logger.LogWarning(
                    "DocketEntry {EntryId} is blocked: requirement {Requirement} is recorded but not " +
                    "implemented in this version, so no decision on it can be accepted",
                    entryId, requirement);

                // The card still goes out, and it says on its face that the entry is blocked — a
                // blocked entry never claims a confirmation is being awaited.
                var blockedRequest = await EvidenceCardRequestFactory.CreateAsync(
                    docketStore, entryId, context.Affidavit, expiresAt, cancellationToken);
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
                docketStore, entryId, context.Affidavit, expiresAt, cancellationToken);
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

    /// <summary>
    /// Routes a human decision that names nobody — the shape every release before the decision
    /// record existed had.
    /// </summary>
    /// <remarks>
    /// Equivalent to passing <see cref="DecisionAct.Unattributed"/>: no tenant is compared, no reason
    /// is recorded, and a late decision's amendments are not preserved because the row would have
    /// nobody to attribute the correction to. Prefer the overload that takes a
    /// <see cref="DecisionAct"/>; this one exists so hosts on the previous release keep compiling.
    /// </remarks>
    /// <param name="entryId">The entry being decided.</param>
    /// <param name="decision">Approve or reject.</param>
    /// <param name="amendments">Fields the reviewer changed. Ignored on rejection.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    public Task<(ReviewOutcome? Outcome, DateTimeOffset? EntryCreatedAt)> HandleDecisionAsync(
        Guid entryId,
        ApprovalDecision decision,
        IReadOnlyDictionary<string, object?>? amendments,
        CancellationToken cancellationToken)
        => HandleDecisionAsync(entryId, decision, DecisionAct.Unattributed, amendments, cancellationToken);

    /// <inheritdoc cref="HandleDecisionAsync(Guid, ApprovalDecision, IReadOnlyDictionary{string, object?}?, CancellationToken)"/>
    /// <param name="entryId">The entry being decided.</param>
    /// <param name="decision">Approve or reject.</param>
    /// <param name="amendments">Fields the reviewer changed. Ignored on rejection.</param>
    public Task<(ReviewOutcome? Outcome, DateTimeOffset? EntryCreatedAt)> HandleDecisionAsync(
        Guid entryId,
        ApprovalDecision decision,
        IReadOnlyDictionary<string, object?>? amendments)
        => HandleDecisionAsync(entryId, decision, DecisionAct.Unattributed, amendments, CancellationToken.None);

    /// <inheritdoc cref="HandleDecisionAsync(Guid, ApprovalDecision, IReadOnlyDictionary{string, object?}?, CancellationToken)"/>
    /// <param name="entryId">The entry being decided.</param>
    /// <param name="decision">Approve or reject.</param>
    public Task<(ReviewOutcome? Outcome, DateTimeOffset? EntryCreatedAt)> HandleDecisionAsync(
        Guid entryId,
        ApprovalDecision decision)
        => HandleDecisionAsync(entryId, decision, DecisionAct.Unattributed, null, CancellationToken.None);

    /// <summary>
    /// Routes a human decision to the appropriate handling path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If a <see cref="FileReviewAsync"/> (or <see cref="FileForReviewAsync"/>) task is currently
    /// awaiting a response for <paramref name="entryId"/>, the decision is delivered directly and this
    /// method returns <c>(null, null)</c> — the awaiting caller owns the outcome and completion. If no
    /// waiter exists (the host restarted, or the review was filed through the non-blocking path and
    /// never awaited), the decision is replayed through the Docket under a guarded compare-and-set.
    /// </para>
    /// <para>
    /// <b>Four refusals, four answers.</b> A decision on an entry that is not pending, a decision that
    /// lost a race to a concurrent one, a decision that arrived after the deadline and a decision on a
    /// blocked entry are four different things, and each is reported as its own
    /// <see cref="ReviewOutcome.Refused"/> code rather than all four as an expiry. A late decision
    /// from a caller who identified themselves also has its amendments preserved on the row for a
    /// resubmission — see <paramref name="act"/>.
    /// </para>
    /// </remarks>
    /// <param name="entryId">The entry being decided.</param>
    /// <param name="decision">Approve or reject.</param>
    /// <param name="act">Who decided, in which tenant, and why.</param>
    /// <param name="amendments">
    /// Fields the reviewer changed while acting on the Evidence Card. Ignored on rejection. Carried by
    /// a refused late decision, they are preserved on the row as a separate fact from what an approval
    /// accepted.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    public async Task<(ReviewOutcome? Outcome, DateTimeOffset? EntryCreatedAt)> HandleDecisionAsync(
        Guid entryId,
        ApprovalDecision decision,
        DecisionAct act,
        IReadOnlyDictionary<string, object?>? amendments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(act);

        // Live path: a FileReviewAsync call is awaiting — deliver and let it own the outcome.
        if (transport.TryDeliverResponse(entryId, new EvidenceCardResponse(entryId, decision, Amendments: amendments)))
            return (null, null);

        // Restart path: no live waiter — replay through the Docket.
        var entry = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
        if (entry is null)
        {
            AffiantTelemetry.RecordDecisionUnauthorized(entryId, null, "entry-not-found", DecidePath);
            return (new ReviewOutcome.Refused(entryId, DocketRefusalCodes.EntryNotFound), null);
        }

        // An entry outside the caller's tenant is NOT FOUND, never "forbidden": telling a caller that
        // an id they may not touch exists is the leak the tenant check is for. Until the decision path
        // takes a resolved principal, the tenant is what the caller states; a caller that states none
        // is trusted with the row's own, which is the behaviour every release before this one had.
        if (act.TenantId is { } statedTenant && !string.Equals(statedTenant, entry.TenantId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "HandleDecisionAsync: DocketEntry {EntryId} is outside the caller's tenant", entryId);
            AffiantTelemetry.RecordDecisionUnauthorized(
                entryId, entry.SessionId, "tenant-mismatch", DecidePath);
            return (new ReviewOutcome.Refused(entryId, DocketRefusalCodes.EntryNotFound), null);
        }

        var scope = new DocketScope(entry.TenantId);
        var createdAt = entry.CreatedAt;
        var now = _time.GetUtcNow();

        // A blocked entry refuses every decision, and says which code blocked it. Checked before the
        // store so the refusal carries the marker's own context, which a bare transition result cannot.
        if (entry.Blocked is not null)
        {
            AffiantTelemetry.RecordDecisionUnauthorized(
                entryId, entry.SessionId, "decision-not-pending", DecidePath);
            return (RefuseBlocked(entryId, entry.Blocked), createdAt);
        }

        var decidedAt = act.At ?? now;
        var patch = new DocketTransitionPatch(
            Status: decision == ApprovalDecision.Approved ? ReviewStatus.Approved : ReviewStatus.Rejected,
            Decision: new DecisionRecord(
                decision == ApprovalDecision.Approved ? DecisionKind.Approve : DecisionKind.Reject,
                act.Reason,
                decidedAt),
            // What an approval ACCEPTS. A rejection accepts nothing, so it records nothing here —
            // a refused or rejected caller's edits are a different fact from an approval's.
            Amendments: decision == ApprovalDecision.Approved && amendments is { Count: > 0 }
                ? amendments
                : null,
            // The accepted state those amendments produce — the Affidavit recomputed with the
            // reviewer's values, their act on each amended field's provenance chain, and all three
            // confidence numbers recomputed (AF-4, PV-2). Written BESIDE the proposal, never over
            // it: the row keeps what the agent swore to on Envelope and gains what the reviewer
            // accepted here, so a reader can see both. A map naming a field the Affidavit does not
            // propose is a host defect: it is logged and no amended record is produced, and the
            // decision itself still stands (see FoldAmendments).
            AmendedAffidavit: decision == ApprovalDecision.Approved && amendments is { Count: > 0 }
                ? FoldAmendments(entry.Envelope, amendments, entryId, DeciderOf(entry, act))
                : null,
            DecidedAt: decidedAt);

        var result = await docketStore.TransitionAsync(
            entryId, scope, ReviewStatus.Pending, patch, cancellationToken);

        switch (result)
        {
            case DocketTransitionResult.Transitioned transitioned:
                logger.LogInformation(
                    "HandleDecisionAsync: DocketEntry {EntryId} {Decision} (restart path)", entryId, decision);

                // TL-1 `docket.transition`, emitted by the caller whose own compare-and-set won it.
                AffiantTelemetry.RecordDocketTransition(
                    entryId,
                    entry.SessionId,
                    DocketStateName(ReviewStatus.Pending),
                    DocketStateName(transitioned.Entry.Status),
                    amended: transitioned.Entry.Amendments is { Count: > 0 },
                    execution: transitioned.Entry.Execution?.ToString().ToLowerInvariant(),
                    decisionKind: decision == ApprovalDecision.Approved ? "approve" : "reject",
                    attestationKind: transitioned.Entry.Attestation?.By.Kind);

                return (transitioned.Entry.Status == ReviewStatus.Approved
                    ? new ReviewOutcome.Approved(entryId, transitioned.Entry.AmendedAffidavit)
                    : new ReviewOutcome.Rejected(entryId, act.Reason ?? "No reason provided"),
                    createdAt);

            case DocketTransitionResult.NotFound:
                AffiantTelemetry.RecordDecisionUnauthorized(
                    entryId, entry.SessionId, "entry-not-found", DecidePath);
                return (new ReviewOutcome.Refused(entryId, DocketRefusalCodes.EntryNotFound), createdAt);

            case DocketTransitionResult.Expired:
                return (await HandleLateDecisionAsync(entryId, scope, act, amendments, decidedAt, cancellationToken),
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
                AffiantTelemetry.RecordDecisionUnauthorized(entryId, entry.SessionId, code, DecidePath);
                return (new ReviewOutcome.Refused(entryId, code), createdAt);

            default:
                throw new InvalidOperationException(
                    $"Unknown transition result {result.GetType().Name} for DocketEntry {entryId}.");
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
    /// anything. A caller that identified nobody has nothing to attribute the correction to, so
    /// nothing is preserved — the alternative is a record that cannot say whose correction it is.
    /// </para>
    /// </remarks>
    private async Task<ReviewOutcome> HandleLateDecisionAsync(
        Guid entryId,
        DocketScope scope,
        DecisionAct act,
        IReadOnlyDictionary<string, object?>? amendments,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "HandleDecisionAsync: DocketEntry {EntryId} TTL lapsed before this decision arrived", entryId);

        AffiantTelemetry.RecordDecisionUnauthorized(entryId, null, "decision-expired", DecidePath);

        var preserved = false;
        if (amendments is { Count: > 0 } && act.DecidedBy is { Length: > 0 } principal)
        {
            var outcome = await docketStore.PreserveAmendmentsAsync(
                entryId, scope, amendments, new PreservedAct(decidedAt, principal), cancellationToken);
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

    /// <summary>The refusal a blocked entry answers every act with, carrying the marker's own context.</summary>
    private static ReviewOutcome.Refused RefuseBlocked(Guid entryId, BlockedMarker marker) => marker switch
    {
        BlockedMarker.RequirementNotImplemented r =>
            new ReviewOutcome.Refused(entryId, DocketRefusalCodes.RequirementNotImplemented, r.Level.ToString()),
        BlockedMarker.CoverageRefused c =>
            new ReviewOutcome.Refused(entryId, DocketRefusalCodes.CoverageRefused, c.ToolName),
        _ => new ReviewOutcome.Refused(entryId, marker.Code)
    };

    /// <summary>
    /// Whose correction an accepted amendment records: the act's own principal when the caller named
    /// one, else the entry's reviewer, else the proposer. A record that cannot say whose correction
    /// it is would be worse than one that names the routing hint it had.
    /// </summary>
    private static string DeciderOf(DocketEntry entry, DecisionAct act)
    {
        if (act.DecidedBy is { Length: > 0 } decidedBy) return decidedBy;
#pragma warning disable AFFIANT0001 // the routing hint, until the attestation names the person
        return entry.ReviewerUserId ?? entry.UserId;
#pragma warning restore AFFIANT0001
    }

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
    /// The <c>path</c> attribute value for a refusal raised by <see cref="HandleDecisionAsync(Guid, ApprovalDecision, DecisionAct, IReadOnlyDictionary{string, object?}?, CancellationToken)"/>.
    /// The registry's other two paths — <c>mark-executed</c> and <c>resubmit</c> — arrive with the
    /// execution report and the authorization checks.
    /// </summary>
    private const string DecidePath = "decide";

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
        // `WHERE Status = 'Pending'` (IDocketStore.UpdateReviewStatusAsync's double-submit
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

/// <summary>
/// Who decided a Docket entry, in which tenant, when and why.
/// </summary>
/// <remarks>
/// <para>
/// Bundled into one record rather than added as four parameters so the decision path can grow the
/// facts it carries — a resolved principal, the relay that asserted it, the channel the decision
/// arrived on — without a source break at every host call site each time. The authorization change
/// that follows this one extends this record; it does not re-shape
/// <see cref="ReviewGate.HandleDecisionAsync(Guid, ApprovalDecision, DecisionAct, IReadOnlyDictionary{string, object?}?, CancellationToken)"/>.
/// </para>
/// <para>
/// Every member is optional and an empty act behaves exactly as every release before this one did:
/// the tenant is not compared, the reason is not recorded, and a late decision's amendments are not
/// preserved because there is nobody to attribute them to.
/// </para>
/// </remarks>
/// <param name="DecidedBy">
/// Who the host says decided. Required for a late decision's amendments to be preserved: the
/// preserved record names whose correction it is, and a correction with no author is not one.
/// </param>
/// <param name="TenantId">
/// The tenant the caller is acting in. When given, an entry in another tenant is <em>not found</em>
/// rather than forbidden. When omitted, no comparison is made — the seam the authorization change
/// closes by resolving the principal's tenant itself instead of taking the caller's word.
/// </param>
/// <param name="Reason">The reviewer's stated reason, recorded on the row.</param>
/// <param name="At">When the decision was made. Defaults to the gate's clock.</param>
public sealed record DecisionAct(
    string? DecidedBy = null,
    string? TenantId = null,
    string? Reason = null,
    DateTimeOffset? At = null)
{
    /// <summary>An act that states nothing — the behaviour of every release before the decision record existed.</summary>
    public static DecisionAct Unattributed { get; } = new();
}
