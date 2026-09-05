namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Transport;

/// <summary>
/// Transport-agnostic push/await contract the framework broadcasts and files reviews through.
/// The sole shipped implementation is SignalR (<c>Affiant.Transport.SignalR</c>).
/// </summary>
public interface IStreamingTransport
{
    Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct);

    /// <summary>
    /// Sends <paramref name="payload"/> to every connection currently in <paramref name="groupId"/>.
    /// </summary>
    /// <remarks>
    /// <b>At-least-once, not exactly-once — and no receipt guarantee (Area-5 Decision 3, affiant#28).</b>
    /// A completed, non-faulted task means only that the underlying transport call didn't throw; it
    /// is not evidence that any client received or rendered <paramref name="payload"/>. In
    /// particular, a <paramref name="groupId"/> with zero currently-connected members completes
    /// successfully with zero recipients — the sole shipped implementation
    /// (<c>SignalRStreamingTransport</c>, over ASP.NET Core SignalR) has no server-side way to detect
    /// or report that case; SignalR group membership is not queryable and is not preserved across a
    /// client's reconnect. Callers that need delivery to survive a temporarily-empty group must
    /// re-broadcast rather than rely on this call's completion as a signal — see
    /// <see cref="EvidenceCardRequest"/>'s docs for how the framework does that for the
    /// <see cref="TransportEvent.EvidenceCardRequest"/> path specifically.
    /// </remarks>
    Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct);

    /// <summary>
    /// <b>Document-reserved (P1a, area-4 Decision-1 ruling 2026-08-04) — retired, not deleted.</b>
    /// Blocks until the gate hands over a decision it has already run to a conclusion for
    /// <paramref name="docketId"/>, or until <paramref name="ct"/> is cancelled (either a caller
    /// cancellation or an internal timeout, depending on the token source). Backs
    /// <see cref="Affiant.Core.Services.ReviewGate.FileReviewAsync"/> — see that method's own XML
    /// docs for why this path structurally deadlocks over the framework's only shipped transport
    /// (SignalR, default <c>MaximumParallelInvocationsPerClient = 1</c>; host-apps#25) and is not the
    /// production default. The production default is <see cref="Affiant.Core.Filters.ReviewGateFilter"/>
    /// calling the non-blocking <see cref="Affiant.Core.Services.ReviewGate.FileForReviewAsync"/>
    /// instead (P5a). A sound redesign — the decision traveling on a channel other than the blocked
    /// connection — is tracked in affiant#29 (design ticket, no implementation planned yet).
    /// <para>
    /// What arrives is a <see cref="DecisionHandOff"/> and not a client-shaped response: the
    /// authorization sequence has already run and the row is already written, so a caller that
    /// receives one reports it and writes nothing (AZ-1, AZ-2).
    /// </para>
    /// </summary>
    Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
        string sessionGroupId, Guid docketId, CancellationToken ct = default);

    /// <summary>
    /// Routes a decision the gate has already concluded to the
    /// <see cref="AwaitEvidenceCardResponseAsync"/> call blocking on <paramref name="docketId"/>.
    /// Returns <c>true</c> if a live waiter was found and unblocked; <c>false</c> if no waiter
    /// exists. The default returns <c>false</c> — override in transports that maintain an in-process
    /// waiter registry. Document-reserved alongside <see cref="AwaitEvidenceCardResponseAsync"/>.
    /// </summary>
    /// <remarks>
    /// Delivering a hand-off cannot decide anything: only the gate can mint one, and by the time one
    /// exists the row is already written. A host that calls this is passing on an answer, never
    /// giving one.
    /// </remarks>
    bool TryDeliverResponse(Guid docketId, DecisionHandOff handOff) => false;
}
