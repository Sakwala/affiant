namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IApprovalPolicy
{
    /// <summary>
    /// Evaluate the approval requirement for the given review context.
    /// The <see cref="ReviewContext.Affidavit"/> contains the proposed mutation;
    /// the <see cref="ReviewContext"/> supplies session, tenant, and user identity.
    /// </summary>
    Task<ReviewRequirement> EvaluateAsync(ReviewContext context, CancellationToken ct = default);
}

public enum ReviewRequirement
{
    StandingOrder,
    ReviewerConfirmation,
    ReferralRequired,
    MultiParty
}

public abstract record ReviewResponse;

public sealed record ReviewGranted(
    Guid EntryId,
    Dictionary<string, object>? Amendments
) : ReviewResponse;

public sealed record ReviewDenied(
    Guid EntryId,
    string? Reason
) : ReviewResponse;

public sealed record ReviewExpired(Guid EntryId) : ReviewResponse;
