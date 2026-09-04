namespace Affiant.Core.Tests.Gate;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Xunit;

/// <summary>
/// An approval policy is given the conversation identity the specification always declared it was
/// given — the conversation, the person whose turn produced the proposal, the tenant and the
/// channel — so an order can <em>bind</em> to one of them.
/// </summary>
/// <remarks>
/// The split this file exists to keep visible: identity is supplied so a policy can say what it is
/// <em>about</em> ("only for this member", "only inside this tenant", "only on our own web UI"),
/// and never so it can decide who is entitled to approve. That question is the framework's, enforced
/// through <see cref="IDecisionAuthorizationPolicy"/> before any transition. A policy that read this
/// record as permission would be doing the job an ownership check hand-rolled per host does badly.
/// </remarks>
public class ApprovalPolicyIdentityTests
{
    [Fact]
    public async Task TheChainPassesTheIdentityThrough_ToEveryPolicyItAsks()
    {
        var deferring = new RecordingPolicy(answer: null);
        var answering = new RecordingPolicy(answer: ReviewRequirement.ReviewerConfirmation);
        var evaluator = new ApprovalPolicyEvaluator([deferring, answering]);

        var identity = new ConversationIdentity(
            SessionId: "conversation-1",
            UserId: "ana",
            StartedAt: new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero),
            HostAppName: "meridian",
            TenantId: "tenant-a",
            Channel: "mcp");

        await evaluator.EvaluateAsync(Affidavit(), identity);

        // A rule that has no opinion still needs the identity to work that out.
        Assert.Same(identity, deferring.Seen);
        Assert.Same(identity, answering.Seen);
    }

    [Fact]
    public async Task TheGateSuppliesTheTenantAndTheChannel_FromTheFilingContext()
    {
        var policy = new RecordingPolicy(answer: ReviewRequirement.ReviewerConfirmation);
        var evaluator = new ApprovalPolicyEvaluator([policy]);
        var gate = GateFixture.Create(evaluator);

        await GateFixture.FileAsync(gate, tenantId: "tenant-a", userId: "ana", channel: "mcp");

        var seen = Assert.IsType<ConversationIdentity>(policy.Seen);
        Assert.Equal("tenant-a", seen.TenantId);
        Assert.Equal("mcp", seen.Channel);
        Assert.Equal("ana", seen.UserId);
        Assert.Equal(GateFixture.ConversationId, seen.SessionId);
    }

    [Fact]
    public async Task TheChainStampsThePolicyThatSpoke_OntoTheVerdict()
    {
        var evaluator = new ApprovalPolicyEvaluator(
            [new RecordingPolicy(answer: ReviewRequirement.StandingOrder, version: "2026-09-01")]);

        var verdict = await evaluator.EvaluateAsync(Affidavit(), Identity());

        // Stamped by the chain rather than reported by the policy: a Standing Order's approval is
        // attributed to it on the row, and that record has to be the framework's answer.
        Assert.Equal(typeof(RecordingPolicy).FullName, verdict.PolicyId);
        Assert.Equal("2026-09-01", verdict.PolicyVersion);
    }

    [Fact]
    public async Task TheChainsOwnFallback_NamesNoPolicy_BecauseNoneProducedIt()
    {
        var verdict = await new ApprovalPolicyEvaluator([]).EvaluateAsync(Affidavit(), Identity());

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, verdict.Requirement);
        Assert.Null(verdict.PolicyId);
        Assert.Null(verdict.PolicyVersion);
    }

    private static ConversationIdentity Identity() => new(
        SessionId: "conversation-1",
        UserId: "ana",
        StartedAt: new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero),
        TenantId: "tenant-a",
        Channel: "web");

    private static Affidavit Affidavit() => Abstractions.Models.Affidavit.Create(
        operationType: "CreateOrder",
        entityType: "Order",
        entityId: null,
        fields: [new AffidavitField(
            "title", "Test Order", null,
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "title", 0.8f)))],
        warnings: []);

    private sealed class RecordingPolicy(ReviewRequirement? answer, string? version = null) : IApprovalPolicy
    {
        public ConversationIdentity? Seen { get; private set; }

        public string? PolicyVersion => version;

        public Task<ApprovalVerdict?> EvaluateAsync(
            Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
        {
            Seen = identity;
            return Task.FromResult<ApprovalVerdict?>(
                answer is null ? null : new ApprovalVerdict(answer.Value));
        }
    }
}
