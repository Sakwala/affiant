namespace Affiant.Abstractions.Models;

/// <summary>
/// A reviewer's terminal answer to a filed review, as a closed set of cases:
/// <see cref="ReviewGranted"/>, <see cref="ReviewDenied"/>, <see cref="ReviewExpired"/>.
/// </summary>
public abstract record ReviewResponse;

/// <summary>
/// The reviewer approved the write, optionally amending field values first. A key present with a
/// <c>null</c> value means "clear this field" — the same amendments shape every other amendments
/// carrier in the framework uses (<see cref="DocketEntry.Amendments"/>,
/// <see cref="ReviewContext.Amendments"/>, <c>EvidenceCardResponse.Amendments</c>).
/// </summary>
public sealed record ReviewGranted(
    Guid EntryId,
    IReadOnlyDictionary<string, object?>? Amendments
) : ReviewResponse;

/// <summary>The reviewer rejected the write, optionally recording why.</summary>
public sealed record ReviewDenied(
    Guid EntryId,
    string? Reason
) : ReviewResponse;

/// <summary>No reviewer acted before the entry's expiry deadline elapsed.</summary>
public sealed record ReviewExpired(Guid EntryId) : ReviewResponse;
