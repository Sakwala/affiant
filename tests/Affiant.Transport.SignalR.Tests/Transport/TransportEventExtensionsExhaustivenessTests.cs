namespace Affiant.Transport.SignalR.Tests.Transport;

using Affiant.Abstractions.Transport;
using Affiant.Transport.SignalR.Transport;
using Xunit;

/// <summary>
/// P1c (area-4, ruled 2026-08-04): <see cref="TransportEventExtensions.ToClientEventName"/> is now
/// <c>public</c> and total (an explicit arm per <see cref="TransportEvent"/> member, no
/// <c>default</c>/discard-to-<c>ToString()</c> fallthrough). Two things this class locks that the
/// compiler alone does not:
/// <list type="bullet">
/// <item>
/// The compiler's CS8509 only fires when a NEW named member is added without a matching switch arm
/// — it says nothing about an EXISTING arm's output STRING silently changing (a rename). This test
/// pins the exact current mapping for every member, directly (no reflection needed now that the
/// method is public — this is the P1c "host-assertable without reflection" requirement itself,
/// demonstrated).
/// </item>
/// <item>
/// Proves every named <see cref="TransportEvent"/> value is covered by iterating
/// <see cref="Enum.GetValues{TEnum}"/> rather than hand-listing them a second time — a genuinely
/// missing arm fails the BUILD (CS8509) before this test could ever run, but this test still gives
/// a readable, method-level assertion of the same fact for anyone reading the test suite rather
/// than the build log.
/// </item>
/// </list>
/// </summary>
public class TransportEventExtensionsExhaustivenessTests
{
    private static readonly Dictionary<TransportEvent, string> ExpectedWireNames = new()
    {
        [TransportEvent.EvidenceCardRequest] = "ConfirmAction",
        [TransportEvent.EvidenceCardResponse] = "EvidenceCardResponse",
        [TransportEvent.AgentMessage] = "ReceiveToken",
        [TransportEvent.ContextUpdate] = "ContextUpdated",
        [TransportEvent.SystemNotification] = "SystemNotification",
        [TransportEvent.DocketExpiring] = "DocketExpiring",
        [TransportEvent.DocketExpired] = "DocketExpired",
    };

    [Fact]
    public void EveryNamedTransportEventMember_HasAnExpectedWireNameEntry()
    {
        // If this fails, a TransportEvent member was added/removed without updating
        // ExpectedWireNames above — the test's own coverage, not ToClientEventName()'s (that one
        // is enforced by CS8509 at compile time regardless of this test file).
        var allMembers = Enum.GetValues<TransportEvent>();
        Assert.Equal(allMembers.Length, ExpectedWireNames.Count);
        foreach (var member in allMembers)
            Assert.True(ExpectedWireNames.ContainsKey(member), $"No expected wire name recorded for {member}");
    }

    [Theory]
    [InlineData(TransportEvent.EvidenceCardRequest, "ConfirmAction")]
    [InlineData(TransportEvent.EvidenceCardResponse, "EvidenceCardResponse")]
    [InlineData(TransportEvent.AgentMessage, "ReceiveToken")]
    [InlineData(TransportEvent.ContextUpdate, "ContextUpdated")]
    [InlineData(TransportEvent.SystemNotification, "SystemNotification")]
    [InlineData(TransportEvent.DocketExpiring, "DocketExpiring")]
    [InlineData(TransportEvent.DocketExpired, "DocketExpired")]
    public void ToClientEventName_MatchesPinnedWireName(TransportEvent evt, string expectedWireName)
    {
        // Direct call — no reflection. Before P1c, ToClientEventName() was internal and a host's
        // own contract net could only reach it via reflection (which detects removal, not an
        // output-string change) — this call site is the proof that gap is closed.
        Assert.Equal(expectedWireName, evt.ToClientEventName());
    }

    [Fact]
    public void AllNamedMembers_RoundTripThroughTheSharedLookupTable()
    {
        foreach (var (member, expectedWireName) in ExpectedWireNames)
            Assert.Equal(expectedWireName, member.ToClientEventName());
    }
}
