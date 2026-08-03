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

    // 3 (formerly UserMessage) deleted — area-4 P1a: founding-commit symmetry filler with the
    // AgentMessage member, never specified anywhere in the framework spec, never emitted in
    // production by the framework or either reference host. Inbound chat text enters the framework
    // as a SignalR hub RPC parameter (a host-defined "SendMessage(message, conversationId)" method,
    // SignalR's own idiomatic client→server invoke pattern) — not a broadcast TransportEvent, which
    // this member incorrectly modeled it as. See area-4-d1-fw-intent.md finding A / d1-support-gap.md
    // finding A for the full archaeology. The gap in the numeric sequence is deliberate: TransportEvent
    // is never serialized as its integer value over the wire (SignalRStreamingTransport maps every
    // member to a SignalR method-name string before send), so renumbering the remaining members
    // carries no wire risk either way — leaving the gap avoids a spurious diff on every other member.

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
