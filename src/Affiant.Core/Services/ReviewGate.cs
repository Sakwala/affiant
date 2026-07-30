namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
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
    ILogger<ReviewGate> logger)
{
    private const int DocketTimeoutMinutes = 10;

    /// <summary>
    /// File a review for the given <paramref name="proposal"/> and return the final outcome.
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
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);

        var entryId = context.EntryId ?? Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(DocketTimeoutMinutes);

        try
        {
            // 1. Check for an existing entry (idempotency: same EntryId filed twice).
            var existing = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
            if (existing is not null && existing.Status != ReviewStatus.Pending)
                return MapStatusToOutcome(existing.Status, entryId);

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
                return new ReviewOutcome.Approved(entryId);
            }

            // 4b. ReferralRequired: escalate without client interaction.
            if (requirement == ReviewRequirement.ReferralRequired)
            {
                await docketStore.UpdateReviewStatusAsync(entryId, ReviewStatus.Deferred, cancellationToken);
                logger.LogInformation("Referral required for DocketEntry {EntryId}", entryId);
                return new ReviewOutcome.Referral(entryId, "referral-required");
            }

            // 4c. ReviewerConfirmation / MultiParty: send Evidence Card and await decision.
            var request = new EvidenceCardRequest(entryId, context.Affidavit, expiresAt);
            await transport.BroadcastToGroupAsync(
                context.SessionId, TransportEvent.EvidenceCardRequest, request, cancellationToken);
            logger.LogInformation(
                "Sent EvidenceCardRequest to group {SessionId} for DocketEntry {EntryId}",
                context.SessionId, entryId);

            EvidenceCardResponse response;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(DocketTimeoutMinutes));
                response = await transport.AwaitEventAsync<EvidenceCardResponse>(
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
                    entryId, DocketTimeoutMinutes);
                await docketStore.UpdateReviewStatusAsync(
                    entryId, ReviewStatus.Expired, cancellationToken);
                return new ReviewOutcome.Expired(entryId);
            }

            // 5. Process the reviewer's decision.
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
                    : MapStatusToOutcome(finalEntry.Status, entryId);
            }

            // This call won the approval race — persist the reviewer's amendments (if any) onto
            // the entry it just transitioned. See EvidenceCardResponse.Amendments.
            if (response.Amendments is { Count: > 0 })
            {
                await docketStore.UpdateAmendmentsAsync(entryId, response.Amendments, cancellationToken);
            }

            return new ReviewOutcome.Approved(entryId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("FileReviewAsync cancelled for tool {ToolName}", proposal.ToolName);
            throw;
        }
    }

    /// <summary>
    /// Routes a human decision to the appropriate handling path.
    /// If a <see cref="FileReviewAsync"/> task is currently awaiting a response for
    /// <paramref name="entryId"/>, the decision is delivered directly and this method
    /// returns <c>(null, null)</c> — the awaiting caller owns the outcome and completion,
    /// including persisting <paramref name="amendments"/> (see <see cref="FileReviewAsync"/>).
    /// If no waiter exists (e.g. the host was restarted), the decision is replayed
    /// through the docket store, <paramref name="amendments"/> are persisted directly, and the
    /// outcome plus the entry's creation time are returned.
    /// </summary>
    /// <param name="amendments">
    /// Fields the reviewer changed while acting on the Evidence Card — see
    /// <see cref="EvidenceCardResponse.Amendments"/>. Ignored on rejection.
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
            return (new ReviewOutcome.Expired(entryId), null);
        }

        var createdAt = entry.CreatedAt;
        var newStatus = decision == ApprovalDecision.Approved ? ReviewStatus.Approved : ReviewStatus.Rejected;
        var rowsAffected = await docketStore.UpdateReviewStatusAsync(entryId, newStatus, cancellationToken);
        if (rowsAffected == 0)
        {
            var current = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
            return current is null
                ? (new ReviewOutcome.Expired(entryId), null)
                : (MapStatusToOutcome(current.Status, entryId), createdAt);
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

    /// <summary>
    /// Processes an approval or rejection decision directly from the docket store —
    /// used when no <see cref="FileReviewAsync"/> task is currently awaiting a response
    /// (e.g. the host process was restarted between the Evidence Card being filed and
    /// the reviewer clicking Approve/Reject).
    /// </summary>
    public async Task<ReviewOutcome> ReplayApprovalAsync(
        Guid entryId,
        ApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        var entry = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
        if (entry is null || entry.Status != ReviewStatus.Pending || entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            logger.LogWarning(
                "ReplayApprovalAsync: DocketEntry {EntryId} not found, not pending, or expired", entryId);
            return new ReviewOutcome.Expired(entryId);
        }

        var newStatus = decision == ApprovalDecision.Approved
            ? ReviewStatus.Approved
            : ReviewStatus.Rejected;

        var rowsAffected = await docketStore.UpdateReviewStatusAsync(entryId, newStatus, cancellationToken);
        if (rowsAffected == 0)
        {
            var current = await docketStore.GetDocketEntryAsync(entryId, cancellationToken);
            return current is null
                ? new ReviewOutcome.Expired(entryId)
                : MapStatusToOutcome(current.Status, entryId);
        }

        return decision == ApprovalDecision.Approved
            ? new ReviewOutcome.Approved(entryId)
            : new ReviewOutcome.Rejected(entryId);
    }

    private static ReviewOutcome MapStatusToOutcome(ReviewStatus status, Guid docketId) =>
        status switch
        {
            ReviewStatus.Approved => new ReviewOutcome.Approved(docketId),
            ReviewStatus.Rejected => new ReviewOutcome.Rejected(docketId),
            ReviewStatus.Expired => new ReviewOutcome.Expired(docketId),
            ReviewStatus.Deferred => new ReviewOutcome.Referral(docketId, "deferred"),
            ReviewStatus.Cancelled => new ReviewOutcome.Rejected(docketId, "Cancelled"),
            _ => new ReviewOutcome.Expired(docketId)
        };
}
