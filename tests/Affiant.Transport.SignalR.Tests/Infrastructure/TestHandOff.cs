namespace Affiant.Transport.SignalR.Tests.Infrastructure;

using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;

/// <summary>
/// A decision hand-off, for the tests that exercise the transport's <b>waiter registry</b> — that a
/// delivery finds the call blocked on the same entry id, and that one with no waiter reports so.
/// </summary>
/// <remarks>
/// Only the gate can mint a hand-off in production: the constructor is internal, and this assembly
/// sees it only because <c>Affiant.Abstractions</c> names it in <c>InternalsVisibleTo</c>. A host
/// hub has no such expression, which is what stops a delivery from ever standing in for a decision
/// (AZ-1, AZ-2). What these tests are about is the plumbing underneath that rule.
/// </remarks>
internal static class TestHandOff
{
    public static DecisionHandOff For(Guid entryId, ApprovalDecision decision) => new(
        entryId,
        decision,
        new Attestation(Attestor.Member.Of(new Principal.Member("reviewer-1")), DateTimeOffset.UnixEpoch, entryId),
        decision == ApprovalDecision.Approved
            ? new ReviewOutcome.Approved(entryId)
            : new ReviewOutcome.Rejected(entryId),
        DateTimeOffset.UnixEpoch);
}
