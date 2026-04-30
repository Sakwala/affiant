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
}
