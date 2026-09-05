namespace Affiant.Core.Tests.Gate;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Affiant.Docket.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Filing is scoped and its ids are derived: a caller never learns anything about a row outside its
/// tenant, and the same proposal in the same conversation replays to the row it already has
/// (GT-2, GT-4).
/// </summary>
public sealed class ScopedFilingTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public async Task AReplayFromAnotherTenant_LearnsNothingAboutTheRow()
    {
        var transport = new RecordingBroadcasts();
        var store = new InMemoryDocketStore();
        var gate = Build(transport, store);

        var entryId = Guid.NewGuid();
        var secret = Affidavit.Create(
            "WriteCreate", "Customer", null,
            [new AffidavitField(
                "customerEmail", "acme-ceo@tenant-a.example", null,
                ProvenanceChain.From(ProvenanceTag.FromTool("capture", 0.9f)))],
            warnings: []);

        await gate.FileForReviewAsync(
            new WriteProposal("capture", DateTimeOffset.UnixEpoch, secret),
            Context(TenantA, "session-tenant-a", secret, entryId));

        transport.Broadcasts.Clear();

        // The same entry id, from another tenant, with a different record.
        var mine = Affidavit.Create(
            "WriteCreate", "Customer", null,
            [new AffidavitField(
                "customerEmail", "someone@tenant-b.example", null,
                ProvenanceChain.From(ProvenanceTag.FromTool("capture", 0.9f)))],
            warnings: []);

        await gate.FileForReviewAsync(
            new WriteProposal("capture", DateTimeOffset.UnixEpoch, mine),
            Context(TenantB, "session-tenant-b", mine, entryId));

        // Nothing tenant A swore reaches tenant B's session group.
        foreach (var (_, card) in transport.Broadcasts)
        {
            Assert.DoesNotContain(
                card.Affidavit.Fields,
                f => Equals(f.Value, "acme-ceo@tenant-a.example"));
        }

        // And tenant A's row is untouched.
        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(TenantA, row!.TenantId);
        Assert.Equal("acme-ceo@tenant-a.example", row.Envelope.Fields.Single().Value);
    }

    [Fact]
    public async Task TheSameProposalInTheSameConversation_ReplaysToTheSameRow()
    {
        var store = new InMemoryDocketStore();
        var gate = Build(new RecordingBroadcasts(), store);

        var sworn = OneField("Widget A");
        var first = await gate.FileForReviewAsync(
            new WriteProposal("capture", DateTimeOffset.UnixEpoch, sworn),
            Context(TenantA, "conv-1", sworn));
        var second = await gate.FileForReviewAsync(
            new WriteProposal("capture", DateTimeOffset.UnixEpoch, sworn),
            Context(TenantA, "conv-1", sworn));

        Assert.Equal(
            Assert.IsType<ReviewFilingResult.RequiresReview>(first).EntryId,
            Assert.IsType<ReviewFilingResult.RequiresReview>(second).EntryId);
    }

    [Fact]
    public async Task ADifferentConversation_OrADifferentTenant_IsADifferentRow()
    {
        var store = new InMemoryDocketStore();
        var gate = Build(new RecordingBroadcasts(), store);

        var a = OneField("Widget A");

        var one = await FileAsync(gate, a, TenantA, "conv-1");
        var differentConversation = await FileAsync(gate, a, TenantA, "conv-2");
        var differentTenant = await FileAsync(gate, a, TenantB, "conv-1");

        Assert.Equal(3, new HashSet<Guid> { one, differentConversation, differentTenant }.Count);
    }

    /// <summary>
    /// GT-4's material is the tenant, the conversation, the tool and the canonical form of the
    /// operation AND ITS ARGUMENTS. Two calls that differ in what the model passed are two
    /// proposals; the same call made twice is one row, whatever else the turn did in between.
    /// </summary>
    [Fact]
    public async Task ADifferentArgument_IsADifferentRow_AndTheSameArgumentIsTheSameRow()
    {
        var store = new InMemoryDocketStore();
        var gate = Build(new RecordingBroadcasts(), store);

        var sworn = OneField("Widget A");

        var first = await FileAsync(gate, sworn, TenantA, "conv-1", new() { ["name"] = "Widget A" });
        var other = await FileAsync(gate, sworn, TenantA, "conv-1", new() { ["name"] = "Widget B" });
        var again = await FileAsync(gate, sworn, TenantA, "conv-1", new() { ["name"] = "Widget A" });

        Assert.NotEqual(first, other);
        Assert.Equal(first, again);
    }

    /// <summary>
    /// The other side of the same rule, and the one a host has to know about: two proposals that
    /// carry NO arguments and differ only in a field's value are, by that material, the same
    /// proposal — so the second replays the first's row rather than filing a second. A host whose
    /// writes are told apart by their values passes the arguments the model made them from, which
    /// every seam in this framework does, or supplies its own <c>ReviewContext.EntryId</c>.
    /// </summary>
    [Fact]
    public async Task TwoValuesWithNoArguments_AreTheSameProposal()
    {
        var store = new InMemoryDocketStore();
        var gate = Build(new RecordingBroadcasts(), store);

        var one = await FileAsync(gate, OneField("Widget A"), TenantA, "conv-1");
        var other = await FileAsync(gate, OneField("Widget B"), TenantA, "conv-1");

        Assert.Equal(one, other);
    }

    [Fact]
    public async Task TheRowCarriesTheChannelTheProposalArrivedOn()
    {
        var store = new InMemoryDocketStore();
        var gate = Build(new RecordingBroadcasts(), store);

        var sworn = OneField("Widget A");
        var filed = await FileAsync(gate, sworn, TenantA, "conv-1");

        var row = await store.GetDocketEntryAsync(filed, default);
        Assert.Equal("chat", row!.Channel);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<Guid> FileAsync(
        ReviewGate gate,
        Affidavit sworn,
        string tenantId,
        string conversationId,
        Dictionary<string, object?>? arguments = null)
    {
        var filing = await gate.FileForReviewAsync(
            new WriteProposal("capture", DateTimeOffset.UnixEpoch, sworn, arguments),
            Context(tenantId, conversationId, sworn));
        return Assert.IsType<ReviewFilingResult.RequiresReview>(filing).EntryId;
    }

    private static Affidavit OneField(string value) => Affidavit.Create(
        "WriteCreate", "Widget", null,
        [new AffidavitField("name", value, null, ProvenanceChain.From(ProvenanceTag.FromTool("capture", 0.9f)))],
        warnings: []);

    private static ReviewContext Context(
        string tenantId, string conversationId, Affidavit sworn, Guid? entryId = null) => new(
        SessionId: conversationId,
        TenantId: tenantId,
        UserId: "ana",
        ReviewerUserId: "ana",
        Affidavit: sworn,
        EntryId: entryId,
        Channel: "chat");

    private static ReviewGate Build(IStreamingTransport transport, IDocketStore store) =>
        new(transport, store, new AlwaysReviewerConfirmation(), new AffiantCoreOptions(),
            NullLogger<ReviewGate>.Instance);

    private sealed class AlwaysReviewerConfirmation : IApprovalPolicyEvaluator
    {
        public Task<ApprovalVerdict> EvaluateAsync(
            Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
            => Task.FromResult<ApprovalVerdict>(ReviewRequirement.ReviewerConfirmation);
    }

    private sealed class RecordingBroadcasts : IStreamingTransport
    {
        public List<(string GroupId, EvidenceCardRequest Card)> Broadcasts { get; } = [];

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            if (eventType == TransportEvent.EvidenceCardRequest && payload is EvidenceCardRequest card)
                Broadcasts.Add((groupId, card));

            return Task.CompletedTask;
        }

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => Task.FromCanceled<DecisionHandOff>(new CancellationToken(canceled: true));
    }
}
