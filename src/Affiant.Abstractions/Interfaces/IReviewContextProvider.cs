namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// Host-provided service that builds a <see cref="ReviewContext"/> for a given
/// <see cref="WriteProposal"/>. Implementations source session identity from
/// ambient context (e.g., IHttpContextAccessor, SignalR hub context) and extract
/// the <see cref="Affidavit"/> from the proposal's Envelope property.
/// </summary>
public interface IReviewContextProvider
{
    /// <summary>
    /// Builds the <see cref="ReviewContext"/> for the given write proposal.
    /// Returns null if the current ambient context does not carry sufficient
    /// identity information to file a review (e.g., unauthenticated requests).
    /// </summary>
    ReviewContext? BuildReviewContext(WriteProposal proposal);
}
