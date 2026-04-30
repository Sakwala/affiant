namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Transport;

public interface IStreamingTransport
{
    Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct);
    Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct);
    IAsyncEnumerable<TransportMessage> ReceiveAsync(string connectionId, CancellationToken ct);

    /// <summary>
    /// Block until an event of type <typeparamref name="T"/> for the given
    /// <paramref name="sessionGroupId"/> and <paramref name="docketId"/> is available,
    /// or until <paramref name="ct"/> is cancelled (which signals either a caller
    /// cancellation or an internal timeout, depending on the token source).
    /// </summary>
    Task<T> AwaitEventAsync<T>(string sessionGroupId, Guid docketId, CancellationToken ct = default);

    /// <summary>
    /// Routes a reviewer's decision to the <see cref="AwaitEventAsync{T}"/> call blocking on
    /// <paramref name="docketId"/>. Returns <c>true</c> if a live waiter was found and unblocked;
    /// <c>false</c> if no waiter exists (caller should use the docket-replay path instead).
    /// The default returns <c>false</c> — override in transports that maintain an in-process waiter registry.
    /// </summary>
    bool TryDeliverResponse(Guid docketId, EvidenceCardResponse response) => false;
}
