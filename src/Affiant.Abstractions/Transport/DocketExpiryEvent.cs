namespace Affiant.Abstractions.Transport;

using System.Text.Json.Serialization;
using Affiant.Abstractions;
using Affiant.Abstractions.Models;

/// <summary>
/// What a producer tells a reviewer surface about a Docket entry nobody asked it about: a deadline
/// approaching, a deadline passed, a state change.
///
/// <para>
/// <b>AF-5's shape rule, applied to notifications.</b> The seed's two notifications were told apart
/// by <i>which properties they carried</i> — a payload with an <c>expiresAt</c> was the warning and
/// one without it was the expiry — and a consumer switching on the presence of fields is exactly
/// what AF-5 forbids. From v0.1 they are one discriminated union with a <see cref="Kind"/> property,
/// and each carries the protocol version it conforms to (SR-4).
/// </para>
///
/// <para>
/// A notification is a <b>hint</b>, never a fact a consumer may act on alone: expiry is queryable
/// state, so an entry past its deadline reads expired whether or not any sweep has run or any
/// notification arrived.
/// </para>
/// </summary>
public abstract record DocketNotification
{
    /// <summary>
    /// The protocol version this envelope conforms to (SR-4) — always
    /// <see cref="AffiantProtocol.Version"/>.
    /// </summary>
    public string ProtocolVersion { get; init; } = AffiantProtocol.Version;

    /// <summary>
    /// Which notification this is, as it appears on the wire. Reading it never requires a type test,
    /// and a consumer switches on it rather than on which other properties are present (AF-5).
    /// </summary>
    public abstract string Kind { get; }
}

/// <summary>
/// The three notification kinds, as string constants so a producer and a consumer reference the
/// same literals. Kebab-case is the wire spelling.
/// </summary>
public static class DocketNotificationKind
{
    /// <summary>Discriminator for <see cref="DocketExpiringNotification"/>.</summary>
    public const string DocketExpiring = "docket-expiring";

    /// <summary>Discriminator for <see cref="DocketExpiredNotification"/>.</summary>
    public const string DocketExpired = "docket-expired";

    /// <summary>Discriminator for <see cref="DocketTransitionNotification"/>.</summary>
    public const string DocketTransition = "docket-transition";
}

/// <summary>
/// Payload broadcast via <see cref="TransportEvent.DocketExpiring"/> when a Pending
/// <see cref="DocketEntry"/> is approaching its TTL expiry. The framework's expiry sweep
/// (Affiant.Docket's DocketExpiryService) re-emits this on every tick the entry remains inside
/// the configured warning window — clients must treat repeated notifications for the same
/// <see cref="DocketId"/> as idempotent (e.g. keying a UI countdown off <see cref="ExpiresAt"/>
/// rather than counting notifications received).
/// </summary>
public sealed record DocketExpiringNotification(Guid DocketId, DateTimeOffset ExpiresAt)
    : DocketNotification
{
    /// <inheritdoc />
    public override string Kind => DocketNotificationKind.DocketExpiring;
}

/// <summary>
/// Payload broadcast via <see cref="TransportEvent.DocketExpired"/> when a Pending
/// <see cref="DocketEntry"/> has been transitioned to <see cref="ReviewStatus.Expired"/> — either
/// by the expiry sweep's bulk tick, or by the review gate's own blocking-timeout path.
/// </summary>
public sealed record DocketExpiredNotification(Guid DocketId) : DocketNotification
{
    /// <inheritdoc />
    public override string Kind => DocketNotificationKind.DocketExpired;
}

/// <summary>
/// Payload broadcast when a <see cref="DocketEntry"/> changed state (DK-1) — the state it left, the
/// state it reached, and the execution outcome an approved row now carries.
///
/// <para>
/// New in v0.1 and not yet emitted by any framework path: the guarded transitions that would raise
/// it, and the execution axis an approved row carries, belong to the Docket-row change this one is
/// not stacked on. The shape is declared here so the wire has one spelling of it when that change
/// lands, rather than two.
/// </para>
/// </summary>
/// <param name="DocketId">The entry that changed state.</param>
/// <param name="From">The state the entry left.</param>
/// <param name="To">The state the entry reached.</param>
/// <param name="Execution">
/// What became of the write, or null when the entry is not approved. A separate axis from the
/// status rather than two more statuses, because an approved-but-failed write and an
/// approved-and-committed one differ in what the host must do next, not in whether the approval
/// happened — collapsing them into the status loses the approval.
/// </param>
public sealed record DocketTransitionNotification(
    Guid DocketId,
    ReviewStatus From,
    ReviewStatus To,
    ExecutionOutcome? Execution) : DocketNotification
{
    /// <inheritdoc />
    public override string Kind => DocketNotificationKind.DocketTransition;
}

/// <summary>
/// What became of an approved write once the host's executor reported.
///
/// The framework never performs the write: the only path to <see cref="Executed"/> is the host's
/// own report (AZ-7). Serialized lowercase, the spelling
/// <c>schemas/0.1.0/docket-entry.schema.json</c> freezes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExecutionOutcome>))]
public enum ExecutionOutcome
{
    /// <summary>Approved, and no executor has reported yet.</summary>
    [JsonStringEnumMemberName("unexecuted")]
    Unexecuted,

    /// <summary>The host's executor reported that the write committed.</summary>
    [JsonStringEnumMemberName("executed")]
    Executed,

    /// <summary>The host's executor reported that the write did not commit.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}
