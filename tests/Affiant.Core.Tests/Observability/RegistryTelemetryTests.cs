namespace Affiant.Core.Tests.Observability;

using Affiant.Core.Tests.Gate;
using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Abstractions.Telemetry;
using Affiant.Core.Extensions;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Affiant.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The registry as an emitted contract (rulebook rule TL-1): an event name that is not in the
/// registry must not reach a collector, and neither must an attribute name the registry does not
/// list for that key.
///
/// <para>
/// Two halves. The first drives every <c>AffiantTelemetry.Record*</c> helper directly and checks the
/// whole emitted surface against the shipped registry — the assertion that keeps a typo at an
/// emitting call site from becoming a permanent public name. The second checks each seam that
/// emits, so the registry is not just consistent but actually wired to the gate.
/// </para>
/// </summary>
public class RegistryTelemetryTests
{
    // ── The emitted surface, checked against the registry ────────────────────────────────────

    [Fact]
    public void EveryEventTheHelpersEmit_IsNamedInTheRegistry_WithOnlyItsRegisteredAttributes()
    {
        using var probe = new TelemetryProbe();
        EmitOneOfEverything();

        Assert.NotEmpty(probe.Events);

        foreach (var evt in probe.Events)
        {
            Assert.True(
                TelemetryKeys.Contains(evt.Name),
                $"Event '{evt.Name}' is not in the telemetry-key registry. Every event the gate emits " +
                "is named in the registry (TL-1) — add it to telemetry-keys.json and TelemetryKeys, " +
                "or emit an existing key.");

            var registered = TelemetryKeys.Registry.Keys.Single(k => k.Key == evt.Name).Attributes;
            foreach (var tag in evt.Tags)
            {
                Assert.True(
                    registered.Contains(tag.Key),
                    $"Event '{evt.Name}' carries attribute '{tag.Key}', which its registry entry does " +
                    "not list. Attributes are part of the versioned contract, not free-form.");
            }
        }
    }

    [Fact]
    public void EveryRegistryKey_HasAnEmitterThatCanRaiseIt()
    {
        using var probe = new TelemetryProbe();
        EmitOneOfEverything();

        Assert.Equal(
            TelemetryKeys.All.Order(StringComparer.Ordinal),
            probe.Events.Select(e => e.Name).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AttributesWithNoValueYet_AreAbsent_NotGuessed()
    {
        using var probe = new TelemetryProbe();

        AffiantTelemetry.RecordDocketTransition(Guid.NewGuid(), "conversation-1", "pending", "approved");

        var attributes = probe.Attributes(TelemetryKeys.DocketTransition);
        Assert.DoesNotContain(TelemetryKeys.Attributes.Execution, attributes.Keys);
        Assert.DoesNotContain(TelemetryKeys.Attributes.AttestationKind, attributes.Keys);
        Assert.Equal("pending", attributes[TelemetryKeys.Attributes.From]);
        Assert.Equal("approved", attributes[TelemetryKeys.Attributes.To]);
    }

    /// <summary>
    /// Emits one of each of the nine keys with every attribute this release can supply, so the two
    /// surface checks above have something complete to inspect.
    /// </summary>
    private static void EmitOneOfEverything()
    {
        AffiantTelemetry.RecordAffidavitFiled(
            "CreateOrder", "conversation-1", Guid.NewGuid(), "pending", 3, created: true,
            requirement: "ReviewerConfirmation");
        AffiantTelemetry.RecordSubstanceRefused("CreateOrder", "conversation-1", 3, "no-fields");
        AffiantTelemetry.RecordCoverageRefused("hosted_mcp", "hosted", "wire-up");
        AffiantTelemetry.RecordDocketTransition(
            Guid.NewGuid(), "conversation-1", "pending", "approved",
            amended: true, execution: "unexecuted", decisionKind: "approve", attestationKind: "member");
        AffiantTelemetry.RecordDocketExpired(Guid.NewGuid());
        AffiantTelemetry.RecordDecisionUnauthorized(
            Guid.NewGuid(), "conversation-1", "entry-not-found", "decide", principalKind: "member");
        AffiantTelemetry.RecordStandingOrderFired("Policies.LowRisk", 1, Guid.NewGuid(), "2");
        AffiantTelemetry.RecordStandingOrderBlocked(
            "Policies.LowRisk", "risk-above-threshold", "risk is above the threshold",
            riskScore: 3, riskThreshold: 1, policyVersion: "2",
            provenanceField: "amount", provenanceSource: "Empty", emptyMandatoryFields: "amount");
        AffiantTelemetry.RecordPolicyInvalid("Policies.LowRisk", "evaluate", "it threw", "2");
    }

    // ── The seams ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate_FilingAnEntry_EmitsAffidavitFiled()
    {
        using var probe = new TelemetryProbe();
        var (gate, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var (proposal, context) = CreateInput();

        await gate.FileForReviewAsync(proposal, context);

        var attributes = probe.Attributes(TelemetryKeys.AffidavitFiled);
        Assert.Equal("CreateOrder", attributes[TelemetryKeys.Attributes.GenAiToolName]);
        Assert.Equal("session-test", attributes[TelemetryKeys.Attributes.GenAiConversationId]);
        Assert.Equal("pending", attributes[TelemetryKeys.Attributes.DocketStatus]);
        Assert.Equal(1, attributes[TelemetryKeys.Attributes.AffidavitFieldCount]);
        Assert.Equal(true, attributes[TelemetryKeys.Attributes.Created]);
        Assert.Single(store.Entries);
    }

    [Fact]
    public async Task Gate_ReplayingAFiling_EmitsAffidavitFiledWithCreatedFalse()
    {
        using var probe = new TelemetryProbe();
        var (gate, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var entryId = Guid.NewGuid();
        var (proposal, context) = CreateInput(entryId);

        await gate.FileForReviewAsync(proposal, context);
        await gate.FileForReviewAsync(proposal, context);

        var filings = probe.Events.Where(e => e.Name == TelemetryKeys.AffidavitFiled).ToList();
        Assert.Equal(2, filings.Count);
        Assert.Equal(true, filings[0].Tags.Single(t => t.Key == TelemetryKeys.Attributes.Created).Value);
        Assert.Equal(false, filings[1].Tags.Single(t => t.Key == TelemetryKeys.Attributes.Created).Value);
    }

    [Fact]
    public async Task Gate_StandingOrderAutoApproval_EmitsTheTransition()
    {
        using var probe = new TelemetryProbe();
        var (gate, _) = CreateGate(ReviewRequirement.StandingOrder);
        var (proposal, context) = CreateInput();

        await gate.FileForReviewAsync(proposal, context);

        var attributes = probe.Attributes(TelemetryKeys.DocketTransition);
        Assert.Equal("pending", attributes[TelemetryKeys.Attributes.From]);
        Assert.Equal("approved", attributes[TelemetryKeys.Attributes.To]);
    }

    [Fact]
    public async Task Gate_DecidingAPendingEntry_EmitsTheTransitionWithTheDecisionKind()
    {
        var (gate, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var (proposal, context) = CreateInput();
        var filing = (ReviewFilingResult.RequiresReview)await gate.FileForReviewAsync(proposal, context);

        using var probe = new TelemetryProbe();
        await gate.HandleDecisionAsync(filing.EntryId, ApprovalDecision.Approved, Ctx());

        var attributes = probe.Attributes(TelemetryKeys.DocketTransition);
        Assert.Equal(filing.EntryId.ToString(), attributes[TelemetryKeys.Attributes.EntryId]);
        Assert.Equal("pending", attributes[TelemetryKeys.Attributes.From]);
        Assert.Equal("approved", attributes[TelemetryKeys.Attributes.To]);
        Assert.Equal("approve", attributes[TelemetryKeys.Attributes.DecisionKind]);
        Assert.Equal(false, attributes[TelemetryKeys.Attributes.Amended]);
    }

    [Fact]
    public async Task Gate_DecidingAnEntryThatIsNoLongerPending_EmitsDecisionUnauthorized()
    {
        var (gate, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var (proposal, context) = CreateInput();
        var filing = (ReviewFilingResult.RequiresReview)await gate.FileForReviewAsync(proposal, context);
        await gate.HandleDecisionAsync(filing.EntryId, ApprovalDecision.Approved, Ctx());

        using var probe = new TelemetryProbe();
        await gate.HandleDecisionAsync(filing.EntryId, ApprovalDecision.Approved, Ctx());

        var attributes = probe.Attributes(TelemetryKeys.DecisionUnauthorized);
        Assert.Equal("decision-not-pending", attributes[TelemetryKeys.Attributes.Reason]);
        Assert.Equal("decide", attributes[TelemetryKeys.Attributes.Path]);
        Assert.False(probe.Saw(TelemetryKeys.DocketTransition));
    }

    [Fact]
    public async Task Gate_DecidingAnEntryThatDoesNotExist_EmitsEntryNotFound()
    {
        var (gate, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);

        using var probe = new TelemetryProbe();
        await gate.HandleDecisionAsync(Guid.NewGuid(), ApprovalDecision.Approved, Ctx());

        Assert.Equal(
            "entry-not-found",
            probe.Attributes(TelemetryKeys.DecisionUnauthorized)[TelemetryKeys.Attributes.Reason]);
    }

    [Fact]
    public async Task Gate_DecidingAfterTheDeadline_EmitsDecisionExpired()
    {
        var (gate, store) = CreateGate(
            ReviewRequirement.ReviewerConfirmation,
            new AffiantCoreOptions { DefaultDocketTtl = TimeSpan.FromMilliseconds(1) });
        var (proposal, context) = CreateInput();
        var filing = (ReviewFilingResult.RequiresReview)await gate.FileForReviewAsync(proposal, context);
        store.RewindDeadline(filing.EntryId);

        using var probe = new TelemetryProbe();
        await gate.HandleDecisionAsync(filing.EntryId, ApprovalDecision.Approved, Ctx());

        Assert.Equal(
            "decision-expired",
            probe.Attributes(TelemetryKeys.DecisionUnauthorized)[TelemetryKeys.Attributes.Reason]);
    }

    [Fact]
    public async Task PolicyChain_APolicyThatThrows_EmitsPolicyInvalidAndStillThrows()
    {
        using var probe = new TelemetryProbe();
        var evaluator = new ApprovalPolicyEvaluator([new ThrowingPolicy()]);

        // The host's throw is not swallowed — it becomes a stated refusal carrying wireup-invalid
        // (CV-1) with the original throw as the inner exception, so the tool seam can hand the model
        // an error result instead of letting a raw stack trace escape.
        var refusal = await Assert.ThrowsAsync<AffiantPolicyException>(
            () => evaluator.EvaluateAsync(BuildAffidavit(), TestIdentities.Anyone));
        Assert.Equal("wireup-invalid", refusal.Code);
        Assert.IsType<InvalidOperationException>(refusal.InnerException);

        var attributes = probe.Attributes(TelemetryKeys.PolicyInvalid);
        Assert.Equal(typeof(ThrowingPolicy).FullName, attributes[TelemetryKeys.Attributes.PolicyId]);
        Assert.Equal("evaluate", attributes[TelemetryKeys.Attributes.Option]);
    }

    [Fact]
    public async Task PolicyChain_APolicyThatAnswers_EmitsNothing()
    {
        using var probe = new TelemetryProbe();
        var evaluator = new ApprovalPolicyEvaluator([]);

        var requirement = await evaluator.EvaluateAsync(BuildAffidavit(), TestIdentities.Anyone);

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, requirement!.Requirement);
        Assert.False(probe.Saw(TelemetryKeys.PolicyInvalid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WireUp_AnUnusableDeadline_EmitsPolicyInvalidAndRefuses(int minutes)
    {
        using var probe = new TelemetryProbe();
        var validator = new AffiantWireUpValidator(
            new AffiantCoreOptions { DefaultDocketTtl = TimeSpan.FromMinutes(minutes) },
            NullLogger<AffiantWireUpValidator>.Instance);

        await Assert.ThrowsAsync<AffiantStartupException>(() => validator.StartAsync(CancellationToken.None));

        var attributes = probe.Attributes(TelemetryKeys.PolicyInvalid);
        Assert.Equal(
            $"{nameof(AffiantCoreOptions)}.{nameof(AffiantCoreOptions.DefaultDocketTtl)}",
            attributes[TelemetryKeys.Attributes.Option]);
    }

    [Fact]
    public async Task WireUp_TheDefaultDeadline_IsAccepted()
    {
        using var probe = new TelemetryProbe();
        var validator = new AffiantWireUpValidator(
            new AffiantCoreOptions(), NullLogger<AffiantWireUpValidator>.Instance);

        await validator.StartAsync(CancellationToken.None);

        Assert.False(probe.Saw(TelemetryKeys.PolicyInvalid));
    }

    // ── Test doubles ─────────────────────────────────────────────────────────────────────────

    private sealed class ThrowingPolicy : IApprovalPolicy
    {
        public Task<ApprovalVerdict?> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("this policy is broken");
    }

    private sealed class SilentTransport : IStreamingTransport
    {
        public bool TryDeliverResponse(Guid docketId, DecisionHandOff response) => false;

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
            string sessionId, Guid docketId, CancellationToken ct)
            => Task.FromException<DecisionHandOff>(new OperationCanceledException(ct));
    }

    private sealed class TestDocketStore : IDocketStore
    {
        public Dictionary<Guid, DocketEntry> Entries { get; } = [];

        /// <summary>Moves an entry's deadline into the past, so the next read of the row is a read
        /// of an expired one — how this double reaches the late-decision path.</summary>
        public void RewindDeadline(Guid entryId) =>
            Entries[entryId] = Entries[entryId] with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) };

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => Task.CompletedTask;

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<ConversationContext?>(null);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
        {
            Entries.TryAdd(entry.EntryId, entry);
            return Task.CompletedTask;
        }

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult(Entries.TryGetValue(entryId, out var e) ? e : null);

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
        {
            if (!Entries.TryGetValue(entryId, out var entry) || entry.Status != ReviewStatus.Pending)
                return Task.FromResult(0);
            Entries[entryId] = entry with { Status = status };
            return Task.FromResult(1);
        }

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);


        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>(
                [.. Entries.Values.Where(e => e.Status == ReviewStatus.Pending)]);

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>(
                [.. Entries.Values.Where(e => e.Status == ReviewStatus.Pending && e.ExpiresAt <= expiresBeforeUtc)]);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct) => Task.CompletedTask;
    
        // ── The scoped, guarded, paged surface ──────────────────────────────
        // The two members the decision path actually reaches are implemented; the rest refuse,
        // because a stub that quietly answered would let a test pass against behaviour nobody wrote.

        /// <summary>The guarded compare-and-set, with expiry read as state.</summary>
        Task<DocketTransitionResult> IDocketStore.TransitionAsync(
            Guid entryId, DocketScope scope, ReviewStatus expected, DocketTransitionPatch patch, CancellationToken ct)
        {
            if (!Entries.TryGetValue(entryId, out var entry))
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.NotFound());
            if (entry.Status != ReviewStatus.Pending)
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.AlreadyDecided());
            if (entry.ExpiresAt <= DateTimeOffset.UtcNow && patch.Status != ReviewStatus.Expired)
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.Expired());

            var moved = entry with
            {
                Status = patch.Status,
                Execution = patch.Status == ReviewStatus.Approved
                    ? patch.Execution ?? ExecutionOutcome.Unexecuted
                    : null,
                Decision = patch.Decision,
                Amendments = patch.Amendments ?? entry.Amendments,
                AmendedAffidavit = patch.AmendedAffidavit ?? entry.AmendedAffidavit,
                Attestation = patch.Attestation ?? entry.Attestation,
            };
            Entries[entryId] = moved;
            return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.Transitioned(moved));
        }

        Task<PreserveAmendmentsResult> IDocketStore.PreserveAmendmentsAsync(
            Guid entryId, DocketScope scope, IReadOnlyDictionary<string, object?> amendments,
            PreservedAct act, CancellationToken ct)
        {
            if (!Entries.TryGetValue(entryId, out var entry))
                return Task.FromResult<PreserveAmendmentsResult>(new PreserveAmendmentsResult.NotFound());
            var kept = entry with { PreservedAmendments = new PreservedAmendments(amendments, act.At, act.By) };
            Entries[entryId] = kept;
            return Task.FromResult<PreserveAmendmentsResult>(new PreserveAmendmentsResult.Preserved(kept));
        }

        Task<RecordExecutionResult> IDocketStore.RecordExecutionAsync(
            Guid entryId, DocketScope scope, ExecutionOutcome outcome, string? detail,
            ExecutionOutcome expected, CancellationToken ct)
            => throw new NotSupportedException();

        Task<RecordSupersessionResult> IDocketStore.RecordSupersessionAsync(
            Guid entryId, DocketScope scope, Guid supersededBy, CancellationToken ct)
            => throw new NotSupportedException();

        Task<int> IDocketStore.MarkBlockedAsync(Guid entryId, DocketScope scope, BlockedMarker marker, CancellationToken ct)
            => Task.FromResult(0);

        Task<DocketPageResult<DocketEntry>> IDocketStore.ListPendingAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
            => Task.FromResult(new DocketPageResult<DocketEntry>([], null, false));

        Task<DocketPageResult<DocketEntry>> IDocketStore.ListApprovedUnexecutedAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
            => Task.FromResult(new DocketPageResult<DocketEntry>([], null, false));

        Task<ExpireDueResult> IDocketStore.ExpireDueAsync(
            DateTimeOffset now, DocketScope scope, int limit, CancellationToken ct)
            => Task.FromResult(new ExpireDueResult([], false));

        Task<RetentionResult> IDocketStore.ApplyRetentionAsync(
            DocketRetentionPolicy policy, DocketScope scope, int limit, CancellationToken ct)
            => throw new NotSupportedException();

        Task<int> IDocketStore.PurgeTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotSupportedException();

        IAsyncEnumerable<DocketEntry> IDocketStore.ExportAsync(DocketScope scope, CancellationToken ct)
            => throw new NotSupportedException();
}

    private sealed class FixedRequirementEvaluator(ReviewRequirement requirement) : IApprovalPolicyEvaluator
    {
        public Task<ApprovalVerdict> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
            => Task.FromResult<ApprovalVerdict>(requirement);
    }

    private static (ReviewGate Gate, TestDocketStore Store) CreateGate(
        ReviewRequirement requirement, AffiantCoreOptions? options = null)
    {
        var store = new TestDocketStore();
        var gate = new ReviewGate(
            new SilentTransport(), store, new FixedRequirementEvaluator(requirement),
            options ?? new AffiantCoreOptions(), NullLogger<ReviewGate>.Instance,
            timeProvider: null, new AllowAllDecisionAuthorization());
        return (gate, store);
    }

    /// <summary>A resolved member principal in the entries' own tenant — this file tests events, not authorization.</summary>
    private static DecisionContext Ctx() =>
        new(new Principal.Member("reviewer-456"), "tenant-default", ConversationId: "session-1", Channel: "test");

    private static Affidavit BuildAffidavit() => Affidavit.Create(
        operationType: "CreateOrder",
        entityType: "Order",
        entityId: null,
        fields: [new AffidavitField("title", "Test Order", null,
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "title", 0.8f)))],
        warnings: []);

    private static (WriteProposal Proposal, ReviewContext Context) CreateInput(Guid? entryId = null)
    {
        var affidavit = BuildAffidavit();
        return (
            new WriteProposal("CreateOrder", DateTimeOffset.UtcNow, affidavit),
            new ReviewContext(
                SessionId: "session-test",
                TenantId: "tenant-default",
                UserId: "user-123",
                ReviewerUserId: "reviewer-456",
                Affidavit: affidavit,
                EntryId: entryId));
    }
}
