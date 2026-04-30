namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates the review state machine: file a <see cref="DocketEntry"/>,
/// evaluate the approval policy, optionally send an <see cref="EvidenceCardRequest"/>
/// and await the reviewer's response, then update the final status.
///
/// Parallel implementation alongside ChatHub's existing logic (Story 6.6).
/// Cut-over to single source of truth happens in Story 6.7.
/// </summary>
public sealed class ReviewGate(
    IStreamingTransport transport,
    IDocketStore docketStore,
    IApprovalPolicy approvalPolicy,
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
                    Amendments: null);
                await docketStore.FileDocketEntryAsync(entry, cancellationToken);
                logger.LogInformation(
                    "Filed DocketEntry {EntryId} for tool {ToolName}", entryId, proposal.ToolName);
            }

            // 3. Evaluate the approval policy before involving the reviewer.
            var requirement = await approvalPolicy.EvaluateAsync(context, cancellationToken);

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

            return new ReviewOutcome.Approved(entryId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("FileReviewAsync cancelled for tool {ToolName}", proposal.ToolName);
            throw;
        }
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
