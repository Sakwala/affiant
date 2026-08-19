namespace Affiant.Abstractions.Models;

/// <summary>
/// A reviewer's terminal answer to a filed review, as a closed set of cases:
/// <see cref="ReviewGranted"/>, <see cref="ReviewDenied"/>, <see cref="ReviewExpired"/>.
/// </summary>
public abstract record ReviewResponse;

/// <summary>The reviewer approved the write, optionally amending field values first.</summary>
public sealed record ReviewGranted(
    Guid EntryId,
    Dictionary<string, object>? Amendments
) : ReviewResponse;

/// <summary>The reviewer rejected the write, optionally recording why.</summary>
public sealed record ReviewDenied(
    Guid EntryId,
    string? Reason
) : ReviewResponse;

/// <summary>No reviewer acted before the entry's expiry deadline elapsed.</summary>
public sealed record ReviewExpired(Guid EntryId) : ReviewResponse;
