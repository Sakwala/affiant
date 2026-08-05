namespace Affiant.Core.Services;

using System.Diagnostics;
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
    ILogger<ReviewGate> logger)
{
    /// <summary>
    /// Non-blocking half of filing a review (framework enabler for host issue
    /// affiant-host-apps#25 / triage F0-A1): files the <see cref="DocketEntry"/>, evaluates the
    /// approval policy (auto-approve/referral short-circuits included), and — if a human reviewer
    /// must act — broadcasts the <see cref="EvidenceCardRequest"/>, all without registering a
    /// waiter or blocking on the reviewer's response. Use this when the caller cannot afford to
    /// await a review inline (e.g. a host request pipeline); route the eventual decision to
    /// <see cref="HandleDecisionAsync"/> when it arrives.
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
        => FileForReviewCoreAsync(proposal, context, priorAmendments: null, cancellationToken);

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
    /// a separate hub RPC routed to <see cref="HandleDecisionAsync"/>, never through this method's
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
            await docketStore.UpdateReviewStatusAsync(
                entryId, ReviewStatus.Rejected, cancellationToken);
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
        if (response.Amendments is { Count: > 0 })
        {
            await docketStore.UpdateAmendmentsAsync(entryId, response.Amendments, cancellationToken);
        }

        return new ReviewOutcome.Approved(entryId);
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
    /// <paramref name="expiredEntryId"/> does not identify an existing entry, or the entry's
    /// <see cref="DocketEntry.Status"/> is not <see cref="ReviewStatus.Expired"/>.
    /// </exception>
    /// <remarks>
    /// Lineage back to <paramref name="expiredEntryId"/> is NOT persisted on the new entry —
    /// recording it would require a new column across all three <see cref="IDocketStore"/>
    /// backends (a schema migration), which is out of scope for this change. Both entry ids are
    /// logged together on resubmission so the pair can be correlated from application logs until
    /// a lineage column ships.
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

        var proposal = new WriteProposal(entry.OperationType, DateTimeOffset.UtcNow, entry.Envelope);
        var context = new ReviewContext(
            SessionId: entry.SessionId,
            TenantId: entry.TenantId,
            UserId: entry.UserId,
            // DocketEntry.ReviewerUserId is null for self-reviewed entries (see DocketEntry
            // remarks); ReviewContext requires a non-null reviewer, so self-review falls back
            // to the original proposer.
            ReviewerUserId: entry.ReviewerUserId ?? entry.UserId,
            Affidavit: entry.Envelope,
            EntryId: null);

        var filing = await FileForReviewCoreAsync(proposal, context, entry.Amendments, cancellationToken);

        logger.LogInformation(
            "ResubmitAsync: resubmitted expired DocketEntry {ExpiredEntryId} as a fresh review ({FilingResultType})",
            expiredEntryId, filing.GetType().Name);

        return filing;
    }

    /// <summary>
    /// Shared filing core for <see cref="FileForReviewAsync"/> and <see cref="ResubmitAsync"/>:
    /// idempotency check, DocketEntry filing, policy evaluation (StandingOrder / ReferralRequired
    /// short-circuits), and — when a human reviewer must act — the Evidence Card broadcast. Never
    /// registers a waiter and never blocks on a reviewer response.
    /// </summary>
    private async Task<ReviewFilingResult> FileForReviewCoreAsync(
        WriteProposal proposal,
        ReviewContext context,
        IReadOnlyDictionary<string, object?>? priorAmendments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);

        var entryId = context.EntryId ?? Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.Add(options.DefaultDocketTtl);

        try
        {
            // 1. Check for an existing entry (idempotency: same EntryId filed twice).
            var existing = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
            if (existing is not null && existing.Status != ReviewStatus.Pending)
                return new ReviewFilingResult.Decided(existing.Status.ToReviewOutcome(entryId));

            // 2. File a new entry if one does not already exist.
            if (existing is null)
            {
                var amendments = context.Amendments is { Count: > 0 }
                    ? context.Amendments.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
                    : null;

                var entry = new DocketEntry(
                    EntryId: entryId,
                    SessionId: context.SessionId,
                    TenantId: context.TenantId,
                    UserId: context.UserId,
                    ReviewerUserId: context.ReviewerUserId,
                    OperationType: proposal.ToolName,
                    Envelope: context.Affidavit,
                    Status: ReviewStatus.Pending,
                    CreatedAt: DateTimeOffset.UtcNow,
                    ExpiresAt: expiresAt,
                    Amendments: amendments);
                await docketStore.FileDocketEntryAsync(entry, cancellationToken);
                logger.LogInformation(
                    "Filed DocketEntry {EntryId} for tool {ToolName}", entryId, proposal.ToolName);
            }

            // 3. Evaluate the approval policy pipeline before involving the reviewer.
            var requirement = await evaluator.EvaluateAsync(context.Affidavit, cancellationToken);

            // 4a. StandingOrder: auto-approve without client interaction.
            if (requirement == ReviewRequirement.StandingOrder)
            {
                await docketStore.UpdateReviewStatusAsync(entryId, ReviewStatus.Approved, cancellationToken);
                logger.LogInformation("StandingOrder auto-approved DocketEntry {EntryId}", entryId);
                return new ReviewFilingResult.Decided(new ReviewOutcome.Approved(entryId));
            }

            // 4b. ReferralRequired: escalate without client interaction.
            if (requirement == ReviewRequirement.ReferralRequired)
            {
                await docketStore.UpdateReviewStatusAsync(entryId, ReviewStatus.Deferred, cancellationToken);
                logger.LogInformation("Referral required for DocketEntry {EntryId}", entryId);
                return new ReviewFilingResult.Decided(new ReviewOutcome.Referral(entryId, "referral-required"));
            }

            // 4c. ReviewerConfirmation / MultiParty: send the Evidence Card. No waiter registered
            // here — that is the caller's choice (FileReviewAsync awaits it; FileForReviewAsync
            // callers route the eventual decision through HandleDecisionAsync instead).
            var request = new EvidenceCardRequest(entryId, context.Affidavit, expiresAt, priorAmendments);
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
    /// Routes a human decision to the appropriate handling path.
    /// If a <see cref="FileReviewAsync"/> (or <see cref="FileForReviewAsync"/>) task is currently
    /// awaiting a response for <paramref name="entryId"/>, the decision is delivered directly and
    /// this method returns <c>(null, null)</c> — the awaiting caller owns the outcome and
    /// completion, including persisting <paramref name="amendments"/> (see
    /// <see cref="FileReviewAsync"/>). If no waiter exists (e.g. the host was restarted, or the
    /// review was filed via the non-blocking <see cref="FileForReviewAsync"/> and never awaited),
    /// the decision is replayed through the docket store, <paramref name="amendments"/> are
    /// persisted directly, and the outcome plus the entry's creation time are returned.
    /// </summary>
    /// <param name="amendments">
    /// Fields the reviewer changed while acting on the Evidence Card — see
    /// <see cref="EvidenceCardResponse.Amendments"/>. Ignored on rejection. When the entry is no
    /// longer Pending (or has expired) by the time this arrives, non-empty amendments are still
    /// persisted before returning <see cref="ReviewOutcome.Expired"/> — see
    /// <see cref="ReviewOutcome.Expired.AmendmentsPreserved"/> (framework half of repo issue #8).
    /// </param>
    public async Task<(ReviewOutcome? Outcome, DateTimeOffset? EntryCreatedAt)> HandleDecisionAsync(
        Guid entryId,
        ApprovalDecision decision,
        IReadOnlyDictionary<string, object?>? amendments = null,
        CancellationToken cancellationToken = default)
    {
        // Live path: a FileReviewAsync call is awaiting — deliver and let it own the outcome.
        if (transport.TryDeliverResponse(entryId, new EvidenceCardResponse(entryId, decision, Amendments: amendments)))
            return (null, null);

        // Restart path: no live waiter — replay through the docket store.
        var entry = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
        if (entry is null || entry.Status != ReviewStatus.Pending || entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            logger.LogWarning(
                "HandleDecisionAsync: DocketEntry {EntryId} not found, not pending, or expired", entryId);

            // The entry can no longer transition to Approved/Rejected, but a reviewer may still
            // have made edits before the window lapsed (or before this decision was delivered) —
            // preserve them rather than silently dropping the reviewer's work (issue #8).
            var amendmentsPreserved = false;
            if (entry is not null && amendments is { Count: > 0 })
            {
                await docketStore.UpdateAmendmentsAsync(entryId, amendments, cancellationToken);
                amendmentsPreserved = true;
                logger.LogInformation(
                    "HandleDecisionAsync: persisted late amendments onto non-pending/expired DocketEntry {EntryId}",
                    entryId);
            }

            return (new ReviewOutcome.Expired(entryId, amendmentsPreserved), null);
        }

        var createdAt = entry.CreatedAt;
        var newStatus = decision == ApprovalDecision.Approved ? ReviewStatus.Approved : ReviewStatus.Rejected;
        var rowsAffected = await docketStore.UpdateReviewStatusAsync(entryId, newStatus, cancellationToken);
        if (rowsAffected == 0)
        {
            var current = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
            return current is null
                ? (new ReviewOutcome.Expired(entryId), null)
                : (current.Status.ToReviewOutcome(entryId), createdAt);
        }

        // This call won the transition race — persist the reviewer's amendments (if any).
        if (decision == ApprovalDecision.Approved && amendments is { Count: > 0 })
        {
            await docketStore.UpdateAmendmentsAsync(entryId, amendments, cancellationToken);
        }

        ReviewOutcome outcome = decision == ApprovalDecision.Approved
            ? new ReviewOutcome.Approved(entryId)
            : new ReviewOutcome.Rejected(entryId);
        logger.LogInformation(
            "HandleDecisionAsync: DocketEntry {EntryId} {Decision} (restart path)", entryId, decision);
        return (outcome, createdAt);
    }
}
