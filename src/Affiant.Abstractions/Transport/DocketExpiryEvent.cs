namespace Affiant.Abstractions.Transport;

using Affiant.Abstractions.Models;

/// <summary>
/// Payload broadcast via <see cref="TransportEvent.DocketExpiring"/> when a Pending
/// <see cref="DocketEntry"/> is approaching its TTL expiry. The framework's expiry sweep
/// (Affiant.Docket's DocketExpiryService) re-emits this on every tick the entry remains inside
/// the configured warning window — clients must treat repeated notifications for the same
/// <see cref="DocketId"/> as idempotent (e.g. keying a UI countdown off <see cref="ExpiresAt"/>
/// rather than counting notifications received).
/// </summary>
public sealed record DocketExpiringNotification(Guid DocketId, DateTimeOffset ExpiresAt);

/// <summary>
/// Payload broadcast via <see cref="TransportEvent.DocketExpired"/> when a Pending
/// <see cref="DocketEntry"/> has been transitioned to <see cref="ReviewStatus.Expired"/> — either
/// by the expiry sweep's bulk tick, or by the review gate's own blocking-timeout path.
/// </summary>
public sealed record DocketExpiredNotification(Guid DocketId);
