namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Transport;

/// <summary>
/// Transport-agnostic push/await contract the framework broadcasts and files reviews through.
/// The sole shipped implementation is SignalR (<c>Affiant.Transport.SignalR</c>).
/// </summary>
public interface IStreamingTransport
{
    Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct);
    Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct);

    /// <summary>
    /// <b>Document-reserved (P1a, area-4 Decision-1 ruling 2026-08-04) — retired, not deleted.</b>
    /// Blocks until the reviewer's <see cref="EvidenceCardResponse"/> for <paramref name="docketId"/>
    /// arrives, or until <paramref name="ct"/> is cancelled (either a caller cancellation or an
    /// internal timeout, depending on the token source). Backs
    /// <see cref="Affiant.Core.Services.ReviewGate.FileReviewAsync"/> — see that method's own XML
    /// docs for why this path structurally deadlocks over the framework's only shipped transport
    /// (SignalR, default <c>MaximumParallelInvocationsPerClient = 1</c>; host-apps#25) and is not the
    /// production default. The production default is <see cref="Affiant.Core.Filters.ReviewGateFilter"/>
    /// calling the non-blocking <see cref="Affiant.Core.Services.ReviewGate.FileForReviewAsync"/>
    /// instead (P5a). A sound redesign — the decision traveling on a channel other than the blocked
    /// connection — is tracked in affiant#29 (design ticket, no implementation planned yet).
    /// <para>
    /// De-genericized from <c>AwaitEventAsync&lt;T&gt;</c> (area-4 P1a): the generic type parameter
    /// only ever legally bound <see cref="EvidenceCardResponse"/> — every implementation runtime-threw
    /// for any other <c>T</c>, a compile-time promise the framework never had a second use case to
    /// honor. The signature now states the one real contract directly.
    /// </para>
    /// </summary>
    Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(
        string sessionGroupId, Guid docketId, CancellationToken ct = default);

    /// <summary>
    /// Routes a reviewer's decision to the <see cref="AwaitEvidenceCardResponseAsync"/> call blocking
    /// on <paramref name="docketId"/>. Returns <c>true</c> if a live waiter was found and unblocked;
    /// <c>false</c> if no waiter exists (caller should use the docket-replay path instead).
    /// The default returns <c>false</c> — override in transports that maintain an in-process waiter registry.
    /// Document-reserved alongside <see cref="AwaitEvidenceCardResponseAsync"/> — see its docs.
    /// </summary>
    bool TryDeliverResponse(Guid docketId, EvidenceCardResponse response) => false;
}
