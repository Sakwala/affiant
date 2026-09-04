namespace Affiant.Abstractions.Transport;

using System.Text.Json.Serialization;
using Affiant.Abstractions;
using Affiant.Abstractions.Models;

/// <summary>
/// What became of a review, as the producer reports it back: the entry, the outcome, and — once an
/// executor has reported — what became of the write.
///
/// <para>
/// <b>A decision result is a report, never an authorization</b> (AZ-5). The Docket row is the sole
/// record of approval authority; nothing replayed from this envelope stands in for the row. A host
/// that treated a received <c>approved</c> as permission to write would have moved the authority
/// off the row and onto the network.
/// </para>
///
/// <para>
/// <b>SR-4</b>: it carries <see cref="ProtocolVersion"/> like every other envelope.
/// </para>
///
/// <para>
/// <b>What this envelope does not yet carry, and why.</b> The v0.1 schema requires one more
/// property — <c>attestation</c>, who agreed (AZ-1) — and this record has no place for it. An
/// attestation is a Docket-row concern before it is a wire concern: the three attestor kinds, the
/// rule that a machine caller may never attest as a member, and the write that puts one on the row
/// in the same operation that approves it, all belong to the authorization-and-attestation change
/// this one is not stacked on. Inventing a second attestation type here so that this envelope could
/// validate today would guarantee two of them by the time that change lands. The envelope exists now
/// so the protocol version and the outcome vocabulary have one spelling; the attestation property is
/// added by the change that makes attestations real. This is the one envelope in this change whose
/// v0.1 schema validation is therefore expected to report a missing required property, and the test
/// suite names it rather than skipping it.
/// </para>
/// </summary>
/// <param name="DocketId">The Docket entry this reports on.</param>
/// <param name="Outcome">What became of the review.</param>
/// <param name="Execution">
/// What became of the write, or null when the review did not approve it. Reads
/// <see cref="ExecutionOutcome.Unexecuted"/> until the host's executor reports (AZ-7).
/// </param>
public sealed record DecisionResult(
    Guid DocketId,
    DecisionOutcome Outcome,
    ExecutionOutcome? Execution = null)
{
    /// <summary>
    /// The protocol version this envelope conforms to (SR-4) — always
    /// <see cref="AffiantProtocol.Version"/>.
    /// </summary>
    public string ProtocolVersion { get; init; } = AffiantProtocol.Version;

    /// <summary>
    /// The report for <paramref name="outcome"/>, mapping the gate's own
    /// <see cref="ReviewOutcome"/> union onto the protocol's four-valued outcome vocabulary.
    /// </summary>
    /// <param name="outcome">The outcome the review gate produced.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is a <see cref="ReviewOutcome.Referral"/>. A referral is not a
    /// decision the protocol has a spelling for at v0.1 — its semantics are reserved — and mapping
    /// it onto one of the four that do exist would report a decision nobody made.
    /// </exception>
    public static DecisionResult For(ReviewOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome switch
        {
            ReviewOutcome.Approved approved =>
                new DecisionResult(approved.DocketId, DecisionOutcome.Approved, ExecutionOutcome.Unexecuted),
            ReviewOutcome.Rejected rejected =>
                new DecisionResult(rejected.DocketId, DecisionOutcome.Rejected),
            ReviewOutcome.Expired expired =>
                new DecisionResult(expired.DocketId, DecisionOutcome.Expired),
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome.GetType().Name,
                "The protocol's decision outcomes are approved, rejected, expired and resubmitted. " +
                "A referral has no v0.1 spelling — its semantics are reserved — and reporting it as " +
                "one of the four would claim a decision nobody made."),
        };
    }
}

/// <summary>
/// What became of a review. Serialized lowercase, the spelling
/// <c>schemas/0.1.0/decision-result.schema.json</c> freezes and the demo hosts' own
/// action-decision payloads already use.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DecisionOutcome>))]
public enum DecisionOutcome
{
    /// <summary>A reviewer, or a Standing Order, approved the write.</summary>
    [JsonStringEnumMemberName("approved")]
    Approved,

    /// <summary>A reviewer refused the write.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,

    /// <summary>The review window closed with no decision.</summary>
    [JsonStringEnumMemberName("expired")]
    Expired,

    /// <summary>An expired entry was superseded by a successor carrying its amendments forward.</summary>
    [JsonStringEnumMemberName("resubmitted")]
    Resubmitted,
}
