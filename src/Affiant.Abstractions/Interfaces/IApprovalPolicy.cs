namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IApprovalPolicy
{
    Task<ReviewRequirement> EvaluateAsync(Affidavit envelope, ConversationIdentity identity);
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
