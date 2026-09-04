namespace Affiant.Core.Tests.Gate;

// The gate is tested against the SHIPPED in-memory store, not a hand-rolled fake of it: a fake
// re-implements the guarded transition and the read-time deadline, and the one thing an
// authorization test must not do is prove the gate correct against a store that agrees with it.
using InMemoryDocketStore = Affiant.Docket.Stores.InMemoryDocketStore;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The decision surface, rule by rule: who may decide (AZ-2), what their identity may attest
/// (AZ-1, AZ-3), that the only path to an executed write is the host's own report against an
/// attested row (AZ-5, AZ-7), and that none of it relaxes when the framework is running with
/// nothing but a store (AZ-6).
/// </summary>
/// <remarks>
/// The failure every one of these closes is one shape: a decision path that accepted any entry id
/// from any caller, with a host-side ownership check that compared the reviewer and not the tenant
/// and permitted the act when identity resolution itself failed. A fail-open on unresolved identity
/// is an authorization bypass the moment a real deployment's identity resolution can fail.
/// </remarks>
public class DecisionAuthorizationTests
{
    private const string TenantId = "tenant-a";
    private const string OtherTenant = "tenant-b";

    // ── AZ-2 (i): an unresolved principal is refused BEFORE the store is read ──────────────────

    [Fact]
    public async Task NoResolvedPrincipal_IsRefused_BeforeTheDocketIsReadAtAll()
    {
        var (gate, _, store) = CreateGate();
        var entryId = await FilePendingAsync(gate);
        store.Reads.Clear();

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, new DecisionContext(Principal: null, TenantId));

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionUnauthorized, refused.Code);

        // "Identity unknown" is never "allow" — and a read that happened before the refusal is a
        // read an attacker can time.
        Assert.Empty(store.Reads);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Pending, row!.Status);
        Assert.Null(row.Attestation);
    }

    [Fact]
    public async Task NoResolvedPrincipal_NeverReachesTheHostsAuthorizationPort()
    {
        var authorization = new AllowAllDecisionAuthorization();
        var (gate, _, _) = CreateGate(authorization: authorization);
        var entryId = await FilePendingAsync(gate);

        await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, new DecisionContext(Principal: null, TenantId));

        // The port is asked about principals, and there was none to ask about.
        Assert.Equal(0, authorization.Calls);
    }

    // ── AZ-2 (ii): the tenant is the boundary, and a miss is a miss ────────────────────────────

    [Fact]
    public async Task AnEntryInAnotherTenant_IsNotFound_NotForbidden()
    {
        var (gate, _, store) = CreateGate();
        var entryId = await FilePendingAsync(gate);

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, Ctx(tenantId: OtherTenant));

        // Telling a caller that an id they may not touch exists is the leak this check closes: the
        // answer is the one an id that was never filed gets.
        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.EntryNotFound, refused.Code);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Pending, row!.Status);
        Assert.Null(row.Attestation);
    }

    [Fact]
    public async Task AScopeBlindStore_DoesNotMakeTheGateFallOpen()
    {
        // The rule says the framework compares the row's tenant itself rather than trusting a
        // store's scope. A store with a scope bug is exactly the case that check exists for: this
        // one answers every read, from any tenant, with the row.
        var (gate, _, inner) = CreateGate();
        var entryId = await FilePendingAsync(gate);

        var blind = new ScopeBlindDocketStore(inner);
        var blindGate = new ReviewGate(
            new SilentTransport(), blind, new FixedVerdictEvaluator(ReviewRequirement.ReviewerConfirmation),
            new AffiantCoreOptions(), NullLogger<ReviewGate>.Instance,
            timeProvider: null, new AllowAllDecisionAuthorization());

        var (outcome, _) = await blindGate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, Ctx(tenantId: OtherTenant));

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.EntryNotFound, refused.Code);
        Assert.Equal(ReviewStatus.Pending, (await inner.GetDocketEntryAsync(entryId, default))!.Status);
    }

    // ── AZ-2 (iii): the host's own answer, and what a broken port means ────────────────────────

    [Fact]
    public async Task APrincipalTheHostDeclines_IsRefused()
    {
        var (gate, _, store) = CreateGate(authorization: new DeclineAllDecisionAuthorization());
        var entryId = await FilePendingAsync(gate);

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, Ctx());

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionUnauthorized, refused.Code);
        Assert.Equal(ReviewStatus.Pending, (await store.GetDocketEntryAsync(entryId, default))!.Status);
    }

    [Fact]
    public async Task AnAuthorizationPortThatThrows_IsARefusal_NeverAnApproval()
    {
        var (gate, _, store) = CreateGate(authorization: new ThrowingDecisionAuthorization());
        var entryId = await FilePendingAsync(gate);

        // The host's callback fell over. It has not said yes — and the caller gets a refusal, not
        // an exception it has to know to catch.
        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, Ctx());

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionUnauthorized, refused.Code);
        Assert.Equal(ReviewStatus.Pending, (await store.GetDocketEntryAsync(entryId, default))!.Status);
    }

    [Fact]
    public async Task TheDefaultAuthorization_RefusesEverything_SoNothingIsEverFailOpen()
    {
        // A host that registers no IDecisionAuthorizationPolicy gets the deny-all. The startup
        // validator refuses such a host, but the runtime must not be open in the meantime.
        var transport = new SilentTransport();
        var store = new RecordingReadsDocketStore(new InMemoryDocketStore());
        var gate = new ReviewGate(
            transport, store, new FixedVerdictEvaluator(ReviewRequirement.ReviewerConfirmation),
            new AffiantCoreOptions(), NullLogger<ReviewGate>.Instance);

        var entryId = await FilePendingAsync(gate);
        var (outcome, _) = await gate.HandleDecisionAsync(entryId, ApprovalDecision.Approved, Ctx());

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionUnauthorized, refused.Code);
    }

    // ── AZ-1 / AZ-3: what identity may attest what ────────────────────────────────────────────

    [Fact]
    public async Task AMemberDecision_AttestsMember_NamingThePersonTheInstantAndTheEntry()
    {
        var (gate, _, store) = CreateGate();
        var entryId = await FilePendingAsync(gate);

        await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, Ctx(reason: "looks right"));

        var row = await store.GetDocketEntryAsync(entryId, default);
        var attestation = Assert.IsType<Attestation>(row!.Attestation);
        var member = Assert.IsType<Attestor.Member>(attestation.By);
        Assert.Equal("ana", member.Id);
        Assert.Equal("member", member.Kind);
        // The instant is the gate's own reading: an attestation says when the implementation
        // observed the act, not when a caller said it happened (AZ-1).
        Assert.InRange(
            attestation.At, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(entryId, attestation.EntryId);
        Assert.Equal(ReviewStatus.Approved, row.Status);
        Assert.Equal(ExecutionOutcome.Unexecuted, row.Execution);
    }

    /// <summary>
    /// Sequence C: a person answered the card on the relay's channel, so the relay decides on their
    /// behalf. The record names both — it must not read as though the person signed in directly.
    /// </summary>
    [Fact]
    public async Task ARelayedDecision_AttestsMemberViaRelay_NamingBothThePersonAndTheRelay()
    {
        var (gate, _, store) = CreateGate();
        var entryId = await FilePendingAsync(gate);

        var relay = new Principal.Service(
            "whatsapp-relay",
            new RelayAssertion("+94770000000", "wamid-1"),
            AssertedMember: "ana");

        await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, Ctx(principal: relay, channel: "mcp"));

        var row = await store.GetDocketEntryAsync(entryId, default);
        var relayed = Assert.IsType<Attestor.MemberViaRelay>(row!.Attestation!.By);
        Assert.Equal("member-via-relay", relayed.Kind);
        Assert.Equal("ana", relayed.MemberId);
        Assert.Equal("whatsapp-relay", relayed.Relay.Principal);
        Assert.Equal("+94770000000", relayed.Relay.ChannelIdentity);
        Assert.Equal("wamid-1", relayed.Relay.MessageId);
    }

    /// <summary>
    /// Sequence C: a machine caller with nobody to speak for and no relay assertion to carry is
    /// refused. The strongest attestation a service principal can honestly make is
    /// member-via-relay, and it has neither half of one.
    /// </summary>
    [Theory]
    [InlineData(null, null)]        // acting on its own behalf
    [InlineData("ana", null)]       // names a person, carries no message
    [InlineData(null, "wamid-1")]   // carries a message, names nobody
    public async Task AMachineCallerWithNothingToRelay_MayNotAttest_AndTheRowIsUntouched(
        string? assertedMember, string? messageId)
    {
        var (gate, _, store) = CreateGate();
        var entryId = await FilePendingAsync(gate);

        var service = new Principal.Service(
            "whatsapp-relay",
            messageId is null ? null : new RelayAssertion("+94770000000", messageId),
            assertedMember);

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, Ctx(principal: service));

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionUnauthorized, refused.Code);
        Assert.Equal("AZ-3", refused.Detail);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Pending, row!.Status);
        Assert.Null(row.Attestation);
        Assert.Null(row.Decision);
        Assert.Null(row.PreservedAmendments);
    }

    /// <summary>
    /// Sequence C: the relay is trusted and its assertion is well formed, and the entry belongs to
    /// another tenant. The tenant is checked before the host's port is consulted.
    /// </summary>
    [Fact]
    public async Task ARelayedDecisionOnAnotherTenantsEntry_IsNotFound()
    {
        var authorization = new AllowAllDecisionAuthorization();
        var (gate, _, store) = CreateGate(authorization: authorization);
        var entryId = await FilePendingAsync(gate);

        var relay = new Principal.Service(
            "whatsapp-relay",
            new RelayAssertion("+94770000000", "wamid-1"),
            AssertedMember: "ana");

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, Ctx(principal: relay, tenantId: OtherTenant));

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.EntryNotFound, refused.Code);
        Assert.Equal(0, authorization.Calls);
        Assert.Equal(ReviewStatus.Pending, (await store.GetDocketEntryAsync(entryId, default))!.Status);
    }

    // ── AZ-1: a Standing Order attests in the same operation that files it approved ────────────

    [Fact]
    public async Task AStandingOrderApproval_IsAttestedToThePolicyAndItsVersion()
    {
        var (gate, _, store) = CreateGate(
            evaluator: new FixedVerdictEvaluator(new ApprovalVerdict(
                ReviewRequirement.StandingOrder,
                PolicyId: "orders.auto-approve",
                PolicyVersion: "2026-09-01")));

        var entryId = await FilePendingAsync(gate);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Approved, row!.Status);

        // Nobody decided, so there is no decision record — but there is never an approved write
        // with no attribution.
        Assert.Null(row.Decision);
        var order = Assert.IsType<Attestor.StandingOrder>(row.Attestation!.By);
        Assert.Equal("standing-order", order.Kind);
        Assert.Equal("orders.auto-approve", order.PolicyId);
        Assert.Equal("2026-09-01", order.Version);
    }

    [Fact]
    public async Task AStandingOrderThatVersionsNothing_RecordsThatItDoesNot()
    {
        var (gate, _, store) = CreateGate(
            evaluator: new FixedVerdictEvaluator(new ApprovalVerdict(
                ReviewRequirement.StandingOrder, PolicyId: "orders.auto-approve")));

        var entryId = await FilePendingAsync(gate);

        var order = Assert.IsType<Attestor.StandingOrder>(
            (await store.GetDocketEntryAsync(entryId, default))!.Attestation!.By);

        // "This policy does not version itself" is a different fact from "the version was lost".
        Assert.Equal(Attestor.StandingOrder.Unversioned, order.Version);
    }

    // ── AZ-5 / AZ-7: the executor is reachable only through an attested row ────────────────────

    [Fact]
    public async Task AnExecutionReport_IsTheOnlyPathToExecuted_AndItRunsTheSameChecks()
    {
        var (gate, _, store) = CreateGate();
        var entryId = await FilePendingAsync(gate);
        await gate.HandleDecisionAsync(entryId, ApprovalDecision.Approved, Ctx());

        // An unresolved principal is refused here exactly as it is on a decision.
        var unauthorized = await gate.MarkExecutedAsync(
            entryId, ExecutionOutcome.Executed, "order-9", new DecisionContext(null, TenantId));
        Assert.Equal(
            DocketRefusalCodes.DecisionUnauthorized,
            Assert.IsType<ReviewOutcome.Refused>(unauthorized).Code);
        Assert.Equal(
            ExecutionOutcome.Unexecuted,
            (await store.GetDocketEntryAsync(entryId, default))!.Execution);

        // A machine caller IS admitted here: reporting an outcome is a statement of fact about work
        // the host performed, which a decision is not.
        var reported = await gate.MarkExecutedAsync(
            entryId, ExecutionOutcome.Executed, "order-9",
            Ctx(principal: new Principal.Service("outbox-worker")));

        Assert.IsType<ReviewOutcome.Approved>(reported);
        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Approved, row!.Status);
        Assert.Equal(ExecutionOutcome.Executed, row.Execution);
        Assert.Equal("order-9", row.ExecutionDetail);
    }

    [Fact]
    public async Task AnExecutionReportOnAPendingRow_IsRefused_ThereIsNoAuthorisedWrite()
    {
        var (gate, _, store) = CreateGate();
        var entryId = await FilePendingAsync(gate);

        var outcome = await gate.MarkExecutedAsync(
            entryId, ExecutionOutcome.Executed, null, Ctx());

        Assert.Equal(
            DocketRefusalCodes.DecisionNotPending,
            Assert.IsType<ReviewOutcome.Refused>(outcome).Code);
        Assert.Null((await store.GetDocketEntryAsync(entryId, default))!.Execution);
    }

    [Fact]
    public async Task TheExecutionOutcomeIsRecordedOnce_AndASecondReportIsRefused()
    {
        var (gate, _, store) = CreateGate();
        var entryId = await FilePendingAsync(gate);
        await gate.HandleDecisionAsync(entryId, ApprovalDecision.Approved, Ctx());
        await gate.MarkExecutedAsync(entryId, ExecutionOutcome.Executed, "order-9", Ctx());

        var second = await gate.MarkExecutedAsync(entryId, ExecutionOutcome.Failed, "timeout", Ctx());

        // Without the guard an approved-and-committed row could later read failed — an edit in
        // place of a recorded fact, and the loss of the distinction the row exists to keep.
        Assert.Equal(
            DocketRefusalCodes.ExecutionAlreadyRecorded,
            Assert.IsType<ReviewOutcome.Refused>(second).Code);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ExecutionOutcome.Executed, row!.Execution);
        Assert.Equal("order-9", row.ExecutionDetail);
    }

    [Fact]
    public async Task Unexecuted_IsNotAnOutcomeAnExecutorCanReport()
    {
        var (gate, _, _) = CreateGate();
        var entryId = await FilePendingAsync(gate);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => gate.MarkExecutedAsync(entryId, ExecutionOutcome.Unexecuted, null, Ctx()));
    }

    // ── AZ-6: nothing above relaxes when the framework is running degraded ─────────────────────

    /// <summary>
    /// Degraded mode is a gate with no model behind it and a transport that cannot deliver: the
    /// host is limited to deterministic operations, and every authorization rule still holds. None
    /// of the checks is conditional on a port being available, so there is no degraded path that
    /// skips one.
    /// </summary>
    [Theory]
    [InlineData(true)]   // no principal resolved
    [InlineData(false)]  // a principal the host declines
    public async Task WithNoInferenceAndNoWorkingTransport_TheAuthorizationRulesStillHold(
        bool unresolvedPrincipal)
    {
        var store = new RecordingReadsDocketStore(new InMemoryDocketStore());
        var degraded = new ReviewGate(
            new UnavailableTransport(),
            store,
            new FixedVerdictEvaluator(ReviewRequirement.ReviewerConfirmation),
            new AffiantCoreOptions(),
            NullLogger<ReviewGate>.Instance,
            timeProvider: null,
            unresolvedPrincipal ? new AllowAllDecisionAuthorization() : new DeclineAllDecisionAuthorization());

        var entryId = await FilePendingAsync(degraded);

        var context = unresolvedPrincipal ? new DecisionContext(null, TenantId) : Ctx();
        var (outcome, _) = await degraded.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, context);

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionUnauthorized, refused.Code);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Pending, row!.Status);
        Assert.Null(row.Attestation);
    }

    // ── DK-1: a late decision's amendments are preserved under the act's own principal ─────────

    [Fact]
    public async Task ALateDecisionFromAPrincipalWhoCouldHaveDecided_PreservesItsAmendments()
    {
        var (gate, _, store) = CreateGate(options: new AffiantCoreOptions
        {
            DefaultDocketTtl = TimeSpan.FromMilliseconds(1),
        });
        var entryId = await FilePendingAsync(gate);
        await Task.Delay(20);

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId,
            ApprovalDecision.Approved,
            Ctx(),
            new Dictionary<string, object?> { ["title"] = "Corrected" });

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionExpired, refused.Code);
        Assert.Equal("amendments-preserved", refused.Detail);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal("Corrected", row!.PreservedAmendments!.Amendments["title"]);

        // The act's OWN instant and principal: a resubmission binds each prefilled field to it, and
        // dating it to the deadline would place the correction at a moment nobody typed anything.
        // The instant is the gate's reading of when the act reached it, never the caller's claim.
        Assert.True(row.PreservedAmendments.At > row.ExpiresAt);
        Assert.Equal("ana", row.PreservedAmendments.By);
    }

    // ── A resubmission runs the same checks ───────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AResubmission_RunsTheSameChecksADecisionDoes(bool unresolvedPrincipal)
    {
        var (gate, _, store) = CreateGate(options: new AffiantCoreOptions
        {
            DefaultDocketTtl = TimeSpan.FromMilliseconds(1),
        });
        var entryId = await FilePendingAsync(gate);
        await Task.Delay(20);

        var context = unresolvedPrincipal
            ? new DecisionContext(null, TenantId)
            : Ctx(tenantId: OtherTenant);

        var filing = await gate.ResubmitAsync(entryId, context);

        var decided = Assert.IsType<ReviewFilingResult.Decided>(filing);
        var refused = Assert.IsType<ReviewOutcome.Refused>(decided.Outcome);
        Assert.Equal(
            unresolvedPrincipal ? DocketRefusalCodes.DecisionUnauthorized : DocketRefusalCodes.EntryNotFound,
            refused.Code);

        // Nothing was superseded: a caller that could not have decided the entry cannot re-open it.
        Assert.Null((await store.GetDocketEntryAsync(entryId, default))!.ResubmittedTo);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static (ReviewGate Gate, SilentTransport Transport, RecordingReadsDocketStore Store) CreateGate(
        IApprovalPolicyEvaluator? evaluator = null,
        IDecisionAuthorizationPolicy? authorization = null,
        AffiantCoreOptions? options = null)
    {
        var transport = new SilentTransport();
        var store = new RecordingReadsDocketStore(new InMemoryDocketStore());
        var gate = new ReviewGate(
            transport,
            store,
            evaluator ?? new FixedVerdictEvaluator(ReviewRequirement.ReviewerConfirmation),
            options ?? new AffiantCoreOptions(),
            NullLogger<ReviewGate>.Instance,
            timeProvider: null,
            authorization ?? new AllowAllDecisionAuthorization());
        return (gate, transport, store);
    }

    private static DecisionContext Ctx(
        Principal? principal = null,
        string tenantId = TenantId,
        string? channel = "web",
        string? reason = null)
        => new(
            principal ?? new Principal.Member("ana"),
            tenantId,
            ConversationId: "conversation-1",
            Channel: channel,
            Reason: reason);

    private static async Task<Guid> FilePendingAsync(ReviewGate gate)
    {
        var entryId = Guid.NewGuid();
        var affidavit = Affidavit.Create(
            operationType: "CreateOrder",
            entityType: "Order",
            entityId: null,
            fields: [new AffidavitField(
                "title", "Test Order", null,
                ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "title", 0.8f)))],
            warnings: []);

        await gate.FileForReviewAsync(
            new WriteProposal("CreateOrder", DateTimeOffset.UtcNow, affidavit),
            new ReviewContext(
                SessionId: "conversation-1",
                TenantId: TenantId,
                UserId: "ana",
                ReviewerUserId: "ana",
                Affidavit: affidavit,
                EntryId: entryId,
                Channel: "web"));

        return entryId;
    }

    /// <summary>A store that records the reads the gate makes, so "before the store is read" is testable.</summary>
    private sealed class RecordingReadsDocketStore(InMemoryDocketStore inner) : IDocketStore
    {
        public List<Guid> Reads { get; } = [];

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
        {
            Reads.Add(entryId);
            return inner.GetDocketEntryAsync(entryId, ct);
        }

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => inner.SaveContextAsync(sessionId, context, ct);

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => inner.LoadContextAsync(sessionId, ct);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
            => inner.FileDocketEntryAsync(entry, ct);


        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => inner.ConsumeForResubmitAsync(entryId, newEntryId, ct);

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => inner.GetResubmissionParentAsync(entryId, ct);


#pragma warning disable AFFIANT0001 // the unscoped listings, still on the interface for one release
        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => inner.ListPendingBySessionAsync(sessionId, ct);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => inner.ListAllPendingAsync(ct);
#pragma warning restore AFFIANT0001

        public Task<DocketTransitionResult> TransitionAsync(
            Guid entryId, DocketScope scope, ReviewStatus expected, DocketTransitionPatch patch, CancellationToken ct)
            => inner.TransitionAsync(entryId, scope, expected, patch, ct);

        public Task<PreserveAmendmentsResult> PreserveAmendmentsAsync(
            Guid entryId, DocketScope scope, IReadOnlyDictionary<string, object?> amendments,
            PreservedAct act, CancellationToken ct)
            => inner.PreserveAmendmentsAsync(entryId, scope, amendments, act, ct);

        public Task<RecordExecutionResult> RecordExecutionAsync(
            Guid entryId, DocketScope scope, ExecutionOutcome outcome, string? detail,
            ExecutionOutcome expected, CancellationToken ct)
            => inner.RecordExecutionAsync(entryId, scope, outcome, detail, expected, ct);

        public Task<RecordSupersessionResult> RecordSupersessionAsync(
            Guid entryId, DocketScope scope, Guid supersededBy, CancellationToken ct)
            => inner.RecordSupersessionAsync(entryId, scope, supersededBy, ct);

        public Task<int> MarkBlockedAsync(Guid entryId, DocketScope scope, BlockedMarker marker, CancellationToken ct)
            => inner.MarkBlockedAsync(entryId, scope, marker, ct);

        public Task<DocketPageResult<DocketEntry>> ListPendingAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
            => inner.ListPendingAsync(scope, page, ct);

        public Task<DocketPageResult<DocketEntry>> ListApprovedUnexecutedAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
            => inner.ListApprovedUnexecutedAsync(scope, page, ct);

        public Task<ExpireDueResult> ExpireDueAsync(
            DateTimeOffset now, DocketScope scope, int limit, CancellationToken ct)
            => inner.ExpireDueAsync(now, scope, limit, ct);

        public Task<RetentionResult> ApplyRetentionAsync(
            DocketRetentionPolicy policy, DocketScope scope, int limit, CancellationToken ct)
            => inner.ApplyRetentionAsync(policy, scope, limit, ct);

        public Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct)
            => inner.PurgeTenantAsync(tenantId, ct);

        public IAsyncEnumerable<DocketEntry> ExportAsync(DocketScope scope, CancellationToken ct)
            => inner.ExportAsync(scope, ct);
    }

    /// <summary>
    /// A store with a scope bug: it answers every scoped read with the row, whatever tenant was
    /// asked for. The gate's own comparison is what stops it falling open.
    /// </summary>
    private sealed class ScopeBlindDocketStore(RecordingReadsDocketStore inner) : IDocketStore
    {
        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => inner.GetDocketEntryAsync(entryId, ct);

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => inner.SaveContextAsync(sessionId, context, ct);

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => inner.LoadContextAsync(sessionId, ct);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
            => inner.FileDocketEntryAsync(entry, ct);


        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => inner.ConsumeForResubmitAsync(entryId, newEntryId, ct);

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => inner.GetResubmissionParentAsync(entryId, ct);


#pragma warning disable AFFIANT0001
        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => inner.ListPendingBySessionAsync(sessionId, ct);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => inner.ListAllPendingAsync(ct);
#pragma warning restore AFFIANT0001

        /// <summary>The bug: the scope is dropped, so another tenant's row transitions happily.</summary>
        public Task<DocketTransitionResult> TransitionAsync(
            Guid entryId, DocketScope scope, ReviewStatus expected, DocketTransitionPatch patch, CancellationToken ct)
            => inner.TransitionAsync(entryId, new DocketScope(TenantId), expected, patch, ct);

        public Task<PreserveAmendmentsResult> PreserveAmendmentsAsync(
            Guid entryId, DocketScope scope, IReadOnlyDictionary<string, object?> amendments,
            PreservedAct act, CancellationToken ct)
            => inner.PreserveAmendmentsAsync(entryId, new DocketScope(TenantId), amendments, act, ct);

        public Task<RecordExecutionResult> RecordExecutionAsync(
            Guid entryId, DocketScope scope, ExecutionOutcome outcome, string? detail,
            ExecutionOutcome expected, CancellationToken ct)
            => inner.RecordExecutionAsync(entryId, new DocketScope(TenantId), outcome, detail, expected, ct);

        public Task<RecordSupersessionResult> RecordSupersessionAsync(
            Guid entryId, DocketScope scope, Guid supersededBy, CancellationToken ct)
            => inner.RecordSupersessionAsync(entryId, new DocketScope(TenantId), supersededBy, ct);

        public Task<int> MarkBlockedAsync(Guid entryId, DocketScope scope, BlockedMarker marker, CancellationToken ct)
            => inner.MarkBlockedAsync(entryId, scope, marker, ct);

        public Task<DocketPageResult<DocketEntry>> ListPendingAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
            => inner.ListPendingAsync(scope, page, ct);

        public Task<DocketPageResult<DocketEntry>> ListApprovedUnexecutedAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
            => inner.ListApprovedUnexecutedAsync(scope, page, ct);

        public Task<ExpireDueResult> ExpireDueAsync(
            DateTimeOffset now, DocketScope scope, int limit, CancellationToken ct)
            => inner.ExpireDueAsync(now, scope, limit, ct);

        public Task<RetentionResult> ApplyRetentionAsync(
            DocketRetentionPolicy policy, DocketScope scope, int limit, CancellationToken ct)
            => inner.ApplyRetentionAsync(policy, scope, limit, ct);

        public Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct)
            => inner.PurgeTenantAsync(tenantId, ct);

        public IAsyncEnumerable<DocketEntry> ExportAsync(DocketScope scope, CancellationToken ct)
            => inner.ExportAsync(scope, ct);
    }

    private sealed class FixedVerdictEvaluator(ApprovalVerdict verdict) : IApprovalPolicyEvaluator
    {
        public FixedVerdictEvaluator(ReviewRequirement requirement)
            : this(new ApprovalVerdict(requirement)) { }

        public Task<ApprovalVerdict> EvaluateAsync(
            Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
            => Task.FromResult(verdict);
    }

    private sealed class SilentTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => Task.FromException<DecisionHandOff>(new OperationCanceledException(ct));
    }

    /// <summary>Degraded mode's transport: nothing can be delivered, and no rule bends because of it.</summary>
    private sealed class UnavailableTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new InvalidOperationException("no transport is connected");

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new InvalidOperationException("no transport is connected");

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException("no transport is connected");
    }
}
