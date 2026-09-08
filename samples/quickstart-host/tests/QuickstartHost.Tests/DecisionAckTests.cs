namespace QuickstartHost.Tests;

using Affiant.Abstractions.Models;
using QuickstartHost.Hubs;
using Xunit;

/// <summary>
/// What the reviewer's browser is told when the gate refuses their decision. Since 1.0.0-beta.3 a
/// late decision comes back as <c>ReviewOutcome.Refused</c> carrying the code
/// <c>decision-expired</c>, not as <c>ReviewOutcome.Expired</c>; an ack that does not read the
/// refusal reports the entry as still pending and the reviewer is told nothing at all.
/// </summary>
public sealed class DecisionAckTests
{
    private static readonly Guid EntryId = Guid.Parse("2f1c9a7e-0f2d-4a3b-9c11-6d5e4f3a2b10");

    [Fact]
    public void A_late_decision_is_acked_as_expired_with_its_amendments_kept()
    {
        var ack = DecisionAck.From(
            EntryId,
            new ReviewOutcome.Refused(
                EntryId, DocketRefusalCodes.DecisionExpired, "amendments-preserved"));

        Assert.Equal("expired", ack.Outcome);
        Assert.True(ack.AmendmentsPreserved);
    }

    [Fact]
    public void A_late_decision_that_carried_no_amendments_says_so()
    {
        var ack = DecisionAck.From(
            EntryId, new ReviewOutcome.Refused(EntryId, DocketRefusalCodes.DecisionExpired));

        Assert.Equal("expired", ack.Outcome);
        Assert.False(ack.AmendmentsPreserved);
    }

    [Fact]
    public void Every_other_refusal_is_a_refusal_and_never_reads_as_pending()
    {
        var ack = DecisionAck.From(
            EntryId, new ReviewOutcome.Refused(EntryId, DocketRefusalCodes.DecisionNotPending));

        Assert.Equal("refused", ack.Outcome);
        Assert.False(ack.AmendmentsPreserved);
    }
}
