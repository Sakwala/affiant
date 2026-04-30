namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IApprovalPolicy
{
    /// <summary>
    /// Evaluate the approval requirement for the proposed mutation described by <paramref name="affidavit"/>.
    /// Return <c>null</c> to defer to the next policy in the evaluation chain.
    /// Return a <see cref="ReviewRequirement"/> to terminate the chain with that value.
    /// Implementations must be deterministic and stateless (no mutable fields).
    /// </summary>
    Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default);
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
