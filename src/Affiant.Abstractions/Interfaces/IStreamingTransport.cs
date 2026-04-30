namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Transport;

public interface IStreamingTransport
{
    Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct);
    Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct);
    IAsyncEnumerable<TransportMessage> ReceiveAsync(string connectionId, CancellationToken ct);
}
