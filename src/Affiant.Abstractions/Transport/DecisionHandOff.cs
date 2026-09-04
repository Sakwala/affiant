namespace Affiant.Abstractions.Transport;

using Affiant.Abstractions.Models;

/// <summary>
/// A decision the gate has already run to a conclusion, on its way to a call that is blocked
/// waiting for one.
///
/// <para>
/// <b>AZ-1, AZ-2</b> — every decision goes through one core: the principal, the tenant-scoped row,
/// the host's authorization port, the state and blocked checks, and the attestation, in that order
/// and before anything is handed anywhere. This type is what comes out the far end. A waiting
/// <c>ReviewGate.FileReviewAsync</c> is unblocked by the <em>result</em> of that sequence and
/// performs no part of it itself, so a decision the core refused never reaches it at all.
/// </para>
///
/// <para>
/// <b>Only the gate can mint one.</b> The constructor is internal. There is no expression through
/// which a host — a hub reaching for <see cref="Interfaces.IStreamingTransport.TryDeliverResponse"/>,
/// a transport substituting a response of its own — builds a hand-off, so delivering one cannot
/// approve anything. That is the difference between a rule a reviewer has to notice and a rule a
/// compiler enforces.
/// </para>
///
/// <para>
/// It is <b>never on the wire</b>. A reviewer's client sends a decision, not an outcome and not an
/// identity; this record is an in-process hand-off between two calls in the same host.
/// </para>
/// </summary>
public sealed class DecisionHandOff
{
    internal DecisionHandOff(
        Guid entryId,
        ApprovalDecision decision,
        Attestation attestation,
        ReviewOutcome outcome,
        DateTimeOffset createdAt)
    {
        EntryId = entryId;
        Decision = decision;
        Attestation = attestation;
        Outcome = outcome;
        CreatedAt = createdAt;
    }

    /// <summary>The entry that was decided.</summary>
    public Guid EntryId { get; }

    /// <summary>What the reviewer chose.</summary>
    public ApprovalDecision Decision { get; }

    /// <summary>
    /// Who the gate held the decision to, built from the principal and from nothing else (AZ-1,
    /// AZ-3). It names the entry it attests to: a record that cannot name its own subject is not
    /// evidence.
    /// </summary>
    public Attestation Attestation { get; }

    /// <summary>
    /// What the decision came to, as the Docket recorded it. The row is already written when a
    /// hand-off exists, so a waiting caller reports this rather than writing anything of its own.
    /// </summary>
    public ReviewOutcome Outcome { get; }

    /// <summary>When the entry was filed, for a caller that reports the review's duration.</summary>
    public DateTimeOffset CreatedAt { get; }
}
