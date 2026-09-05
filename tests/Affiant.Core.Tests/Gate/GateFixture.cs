namespace Affiant.Core.Tests.Gate;

using InMemoryDocketStore = Affiant.Docket.Stores.InMemoryDocketStore;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A gate over the shipped in-memory store, wired the way a host that has done its homework wires
/// one: a real Docket, a host authorization port, and a transport that says nothing.
/// </summary>
/// <remarks>
/// Shared so a test about one rule does not carry a page of composition to get to it. Tests that
/// are <em>about</em> the wiring build their own.
/// </remarks>
internal static class GateFixture
{
    public const string ConversationId = "conversation-1";
    public const string TenantId = "tenant-a";

    public static ReviewGate Create(
        IApprovalPolicyEvaluator evaluator,
        IDocketStore? store = null,
        IDecisionAuthorizationPolicy? authorization = null,
        AffiantCoreOptions? options = null,
        TimeProvider? timeProvider = null)
        => new(
            new NullTransport(),
            store ?? new InMemoryDocketStore(timeProvider),
            evaluator,
            options ?? new AffiantCoreOptions(),
            NullLogger<ReviewGate>.Instance,
            timeProvider,
            authorization ?? new AllowAllDecisionAuthorization());

    public static async Task<Guid> FileAsync(
        ReviewGate gate,
        string tenantId = TenantId,
        string userId = "ana",
        string? channel = "web",
        Affidavit? affidavit = null,
        Guid? entryId = null)
    {
        var sworn = affidavit ?? Proposal();
        var id = entryId ?? Guid.NewGuid();

        await gate.FileForReviewAsync(
            new WriteProposal("CreateOrder", DateTimeOffset.UtcNow, sworn),
            new ReviewContext(
                SessionId: ConversationId,
                TenantId: tenantId,
                UserId: userId,
                ReviewerUserId: userId,
                Affidavit: sworn,
                EntryId: id,
                Channel: channel));

        return id;
    }

    public static Affidavit Proposal(params AffidavitField[] fields) => Affidavit.Create(
        operationType: "CreateOrder",
        entityType: "Order",
        entityId: null,
        fields: fields.Length > 0
            ? fields
            : [new AffidavitField(
                "title", "Test Order", null,
                ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "title", 0.8f)))],
        warnings: []);

    public static DecisionContext Ctx(
        Principal? principal = null,
        string tenantId = TenantId,
        string? reason = null)
        => new(
            principal ?? new Principal.Member("ana"),
            tenantId,
            ConversationId: ConversationId,
            Channel: "web",
            Reason: reason);

    private sealed class NullTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => Task.FromException<DecisionHandOff>(new OperationCanceledException(ct));
    }
}
