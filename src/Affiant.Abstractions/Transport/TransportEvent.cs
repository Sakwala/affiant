namespace Affiant.Abstractions.Transport;

/// <summary>
/// Enum representing the types of events that flow through the transport layer
/// (SignalR, WebSocket, etc.).
/// </summary>
public enum TransportEvent
{
    /// <summary>Framework sends a review request (Evidence Card) to the UI.</summary>
    EvidenceCardRequest = 0,

    /// <summary>UI sends a review response (approval/rejection) back to the framework.</summary>
    EvidenceCardResponse = 1,

    /// <summary>Chat message from the agent.</summary>
    AgentMessage = 2,

    /// <summary>Chat message from the user.</summary>
    UserMessage = 3,

    /// <summary>Framework notifies UI of context changes.</summary>
    ContextUpdate = 4,

    /// <summary>Framework sends a transient notification (error, warning, success).</summary>
    SystemNotification = 5,

    /// <summary>
    /// Framework warns the UI that a Pending <see cref="Models.DocketEntry"/> is approaching its
    /// TTL expiry. May be re-emitted on successive expiry-sweep ticks while the entry remains
    /// inside the warning window — clients must treat repeats as idempotent. Payload:
    /// <see cref="DocketExpiringNotification"/>.
    /// </summary>
    DocketExpiring = 6,

    /// <summary>
    /// Framework notifies the UI that a Pending <see cref="Models.DocketEntry"/> has transitioned
    /// to <see cref="Models.ReviewStatus.Expired"/> without a reviewer decision. Payload:
    /// <see cref="DocketExpiredNotification"/>.
    /// </summary>
    DocketExpired = 7
}
