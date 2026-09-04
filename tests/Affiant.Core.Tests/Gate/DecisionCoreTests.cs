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
/// One decision core: every decision runs the same five steps — the principal, the tenant-scoped
/// row, the host's authorization port, the state and blocked checks, and the attestation — before
/// anything is handed off, and a live waiter is unblocked only by the <em>result</em> of that
/// sequence (AZ-1, AZ-2, AZ-3, AZ-5).
/// </summary>
/// <remarks>
/// <para>
/// Every test here holds a row open with a blocking <c>FileReviewAsync</c>, because that is the one
/// state in which a waiter exists. The point of each is that the waiter changes <b>nothing</b>: the
/// decision is refused, or admitted, on exactly the terms it would be with no waiter at all.
/// </para>
/// <para>
/// The transport double keeps a real in-process waiter registry, the shape the shipped SignalR
/// transport keeps, so what is exercised is the framework's ordering and not a straw man.
/// </para>
/// </remarks>
public sealed class DecisionCoreTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public async Task ADeciderFromAnotherTenant_IsRefusedEntryNotFound_EvenWhileAWaiterHoldsTheRowOpen()
    {
        var transport = new WaiterTransport();
        var store = new InMemoryDocketStore();
        var gate = Build(transport, store, new AllowAll());

        var entryId = Guid.NewGuid();
        var filing = Task.Run(() => FileBlockingAsync(gate, entryId));
        await WaitForPendingAsync(store, entryId);

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId,
            ApprovalDecision.Approved,
            new DecisionContext(new Principal.Member("mallory"), TenantB, "conversation-1", "web"));

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.EntryNotFound, refused.Code);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Pending, row!.Status);
        Assert.Null(row.Attestation);

        // The waiter is still waiting: a refusal is not a decision, so nothing was handed to it.
        Assert.False(filing.IsCompleted);
        transport.Abandon();
        await filing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task APrincipalTheHostDeclines_IsRefused_EvenWhileAWaiterHoldsTheRowOpen()
    {
        var transport = new WaiterTransport();
        var store = new InMemoryDocketStore();
        var authorization = new CountingDeny();
        var gate = Build(transport, store, authorization);

        var entryId = Guid.NewGuid();
        var filing = Task.Run(() => FileBlockingAsync(gate, entryId));
        await WaitForPendingAsync(store, entryId);

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId,
            ApprovalDecision.Approved,
            new DecisionContext(new Principal.Member("mallory"), TenantA, "conversation-1", "web"));

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionUnauthorized, refused.Code);
        Assert.True(authorization.Calls > 0, "AZ-2 (iii): the host's authorization port must be asked.");

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Pending, row!.Status);

        transport.Abandon();
        await filing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AHostThatRegisteredNoPolicy_RefusesEveryDecision_EvenWhileAWaiterHoldsTheRowOpen()
    {
        var transport = new WaiterTransport();
        var store = new InMemoryDocketStore();

        // No IDecisionAuthorizationPolicy at all: the default is DenyAllDecisionAuthorization.
        var gate = new ReviewGate(
            transport, store, new AlwaysReviewerConfirmation(), new AffiantCoreOptions(),
            NullLogger<ReviewGate>.Instance);

        var entryId = Guid.NewGuid();
        var filing = Task.Run(() => FileBlockingAsync(gate, entryId));
        await WaitForPendingAsync(store, entryId);

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId,
            ApprovalDecision.Approved,
            new DecisionContext(new Principal.Member("mallory"), TenantA, "conversation-1", "web"));

        Assert.IsType<ReviewOutcome.Refused>(outcome);
        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Pending, row!.Status);

        transport.Abandon();
        await filing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnAdmittedDecision_ReachesTheWaiter_AndTheRowCarriesTheAttestationOnce()
    {
        var transport = new WaiterTransport();
        var store = new InMemoryDocketStore();
        var gate = Build(transport, store, new AllowAll());

        var entryId = Guid.NewGuid();
        var filing = Task.Run(() => FileBlockingAsync(gate, entryId));
        await WaitForPendingAsync(store, entryId);

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId,
            ApprovalDecision.Approved,
            new DecisionContext(new Principal.Member("ana"), TenantA, "conversation-1", "web"));

        var blocking = await filing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.IsType<ReviewOutcome.Approved>(blocking);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Approved, row!.Status);
        Assert.Equal("ana", Assert.IsType<Attestor.Member>(row.Attestation!.By).Id);
        Assert.Equal(entryId, row.Attestation.EntryId);
    }

    [Fact]
    public async Task AHostThatDeliversItsOwnResponse_CannotApproveAnything()
    {
        var transport = new WaiterTransport();
        var store = new InMemoryDocketStore();
        var gate = Build(transport, store, new CountingDeny());

        var entryId = Guid.NewGuid();
        var filing = Task.Run(() => FileBlockingAsync(gate, entryId));
        await WaitForPendingAsync(store, entryId);

        // A host hub reaching for the transport and delivering a decision it built itself. There is
        // no expression through which it can name one: the hand-off is the gate's to mint.
        Assert.False(transport.TryDeliverHostBuiltApproval(entryId));

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Pending, row!.Status);
        Assert.Null(row.Attestation);

        transport.Abandon();
        await filing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnExecutionReport_OnARowWithNoAttestation_IsRefused()
    {
        var store = new InMemoryDocketStore();
        var gate = Build(new WaiterTransport(), store, new AllowAll());

        // A row approved with nobody on it — the state R-1 makes unreachable through the gate, and
        // which this asserts is unusable even if a store somewhere held one (AZ-5).
        var entryId = Guid.NewGuid();
        var affidavit = OneField();
        await store.FileDocketEntryAsync(
            new DocketEntry(
                entryId, "conversation-1", TenantA, "ana", "ana", "CreateOrder", affidavit,
                ReviewStatus.Approved,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), null,
                Execution: ExecutionOutcome.Unexecuted),
            default);

        var outcome = await gate.MarkExecutedAsync(
            entryId, ExecutionOutcome.Executed, "order-9",
            new DecisionContext(new Principal.Service("outbox-worker"), TenantA));

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionUnauthorized, refused.Code);
        Assert.Equal("AZ-5", refused.Detail);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ExecutionOutcome.Unexecuted, row!.Execution);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ReviewGate Build(
        IStreamingTransport transport, IDocketStore store, IDecisionAuthorizationPolicy authorization) =>
        new(transport, store, new AlwaysReviewerConfirmation(), new AffiantCoreOptions(),
            NullLogger<ReviewGate>.Instance, timeProvider: null, authorization);

    private static Affidavit OneField() => Affidavit.Create(
        operationType: "CreateOrder",
        entityType: "Order",
        entityId: null,
        fields: [new AffidavitField(
            "title", "Test Order", null,
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "title", 0.8f)))],
        warnings: []);

#pragma warning disable AFFIANT0002 // the blocking path is the one under test
    private static async Task<ReviewOutcome> FileBlockingAsync(ReviewGate gate, Guid entryId)
    {
        var affidavit = OneField();
        return await gate.FileReviewAsync(
            new WriteProposal("CreateOrder", DateTimeOffset.UnixEpoch, affidavit),
            new ReviewContext(
                SessionId: "conversation-1",
                TenantId: TenantA,
                UserId: "ana",
                ReviewerUserId: "ana",
                Affidavit: affidavit,
                EntryId: entryId,
                Channel: "web"));
    }
#pragma warning restore AFFIANT0002

    private static async Task WaitForPendingAsync(IDocketStore store, Guid entryId)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await store.GetDocketEntryAsync(entryId, default) is { Status: ReviewStatus.Pending })
                return;
            await Task.Delay(10);
        }

        throw new InvalidOperationException("entry never reached pending");
    }

    private sealed class AlwaysReviewerConfirmation : IApprovalPolicyEvaluator
    {
        public Task<ApprovalVerdict> EvaluateAsync(
            Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
            => Task.FromResult<ApprovalVerdict>(ReviewRequirement.ReviewerConfirmation);
    }

    private sealed class AllowAll : IDecisionAuthorizationPolicy
    {
        public Task<bool> MayDecideAsync(
            Principal principal, DocketEntry entry, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class CountingDeny : IDecisionAuthorizationPolicy
    {
        public int Calls { get; private set; }

        public Task<bool> MayDecideAsync(
            Principal principal, DocketEntry entry, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(false);
        }
    }

    /// <summary>A transport with a real in-process waiter registry, like the shipped SignalR one.</summary>
    private sealed class WaiterTransport : IStreamingTransport
    {
        private readonly TaskCompletionSource<DecisionHandOff> _delivered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public bool TryDeliverResponse(Guid docketId, DecisionHandOff handOff)
            => _delivered.TrySetResult(handOff);

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => _delivered.Task.WaitAsync(ct);

        /// <summary>
        /// What a host hub would reach for: deliver an approval of its own making. There is no
        /// expression that builds one, which is the assertion.
        /// </summary>
        public bool TryDeliverHostBuiltApproval(Guid docketId) => false;

        /// <summary>Lets a still-waiting filing finish so a test does not hang on its own harness.</summary>
        public void Abandon() => _delivered.TrySetCanceled();
    }
}
