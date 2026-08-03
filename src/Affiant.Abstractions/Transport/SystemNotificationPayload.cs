namespace Affiant.Abstractions.Transport;

/// <summary>
/// Payload sent to the UI as a transient notification (error, warning, success).
/// Transported via <see cref="TransportEvent.SystemNotification"/>.
///
/// <b>P1b (area-4, ruled 2026-08-04):</b> named record replacing the anonymous
/// <c>{ level, message }</c> object previously duplicated at each broadcast call site. The wire
/// shape is unchanged on purpose — camelCase <c>{level, message}</c> — so existing host TypeScript
/// keeps working across the pin bump with no client-side change. <see cref="Level"/> stays a plain
/// <c>string</c>, not a C# enum: its allowed values (<c>"error"</c>, <c>"warning"</c>, <c>"info"</c>)
/// are pinned by the host-apps contract net's closed-set value fixtures (Area-2 P2d), not by a
/// framework-side type, avoiding the int-vs-string enum-serialization inconsistency documented for
/// <see cref="ApprovalDecision"/> vs <see cref="Models.ProvenanceSource"/> (area-4 V1).
/// </summary>
public sealed record SystemNotificationPayload(string Level, string Message);
