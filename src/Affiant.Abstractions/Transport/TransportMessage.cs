namespace Affiant.Abstractions.Transport;

/// <summary>
/// Immutable record representing a message flowing through the transport layer.
/// The payload is JSON-serialized so the transport mechanism (SignalR, WebSocket, etc.)
/// is agnostic to the event structure.
/// </summary>
public record TransportMessage(
    string MessageId,
    string SessionId,
    TransportEvent EventType,
    string EventPayload,
    DateTimeOffset Timestamp)
{
    public TransportMessage(string sessionId, TransportEvent eventType, string eventPayload)
        : this(
            Guid.NewGuid().ToString(),
            sessionId,
            eventType,
            eventPayload,
            DateTimeOffset.UtcNow)
    {
    }
}
