namespace Affiant.Core.Tests.Services;

// The gate is tested against the SHIPPED in-memory store, not a hand-rolled fake of it. A fake
// re-implements the guarded transition, the read-time deadline and the paged listings, and the one
// thing a gate test must not do is prove the gate correct against a store that agrees with it and
// with nothing else.
using Affiant.Abstractions;
using InMemoryDocketStore = Affiant.Docket.Stores.InMemoryDocketStore;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Tests.Gate;
using Affiant.Core.Extensions;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

/// <summary>
/// Unit tests for the <see cref="ReviewGate"/> state machine.
/// Uses inline test doubles (FakeStreamingTransport, InMemoryDocketStore, FakeApprovalPolicy).
/// TODO (Story 6.12): Replace inline doubles with shared fixtures from Affiant.TestInfrastructure.
/// </summary>
// The blocking review path is deprecated (AFFIANT0002) and kept for one release; the tests that
// pin its behaviour are the reason it still has to work.
#pragma warning disable AFFIANT0002
public class ReviewGateTests
{
    /// <summary>
    /// Files a row and runs it out of time the only way a row leaves pending: the guarded
    /// transition. Expiry carries no attestation — nobody decided it (AZ-1).
    /// </summary>
    private static async Task FileExpiredAsync(IDocketStore store, DocketEntry entry)
    {
        await store.FileDocketEntryAsync(entry, default);
        await store.TransitionAsync(
            entry.EntryId, new DocketScope(entry.TenantId), ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Expired), default);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeStreamingTransport : IStreamingTransport
    {
        private readonly Queue<Func<Guid, Task>> _scripted = new();
        private readonly TaskCompletionSource<DecisionHandOff> _delivered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _simulateTimeout;
        private bool _hangUntilCancelled;
        private Func<Task>? _beforeTimeoutThrow;

        public List<(string GroupId, TransportEvent EventType, object Payload)> SentEvents { get; } = [];
        public List<DecisionHandOff> DeliveredHandOffs { get; } = [];

        /// <summary>When true, <see cref="TryDeliverResponse"/> simulates a live waiter.</summary>
        public bool HasLiveWaiter { get; set; }

        private int _failNextEvidenceCardBroadcasts;

        /// <summary>Total EvidenceCardRequest broadcast attempts (successful AND failed).</summary>
        public int EvidenceCardBroadcastAttempts { get; private set; }

        /// <summary>
        /// The next <paramref name="count"/> EvidenceCardRequest broadcasts throw instead of
        /// succeeding (P1a broadcast-retry test double). Does not affect other event types —
        /// SystemNotification must still get through so the best-effort notify path is observable.
        /// </summary>
        public void FailNextEvidenceCardBroadcasts(int count) => _failNextEvidenceCardBroadcasts = count;

        /// <summary>
        /// A reviewer who decides the moment the card is filed. The decision goes through the gate —
        /// the only thing that can conclude one — so what unblocks the awaiting call is the
        /// <em>result</em> of the authorization sequence and never a response a test wrote by hand
        /// (AZ-1, AZ-2).
        /// </summary>
        public void EnqueueDecision(
            ReviewGate gate,
            ApprovalDecision decision,
            DecisionContext context,
            string? reason = null,
            IReadOnlyDictionary<string, object?>? amendments = null)
        {
            HasLiveWaiter = true;
            _scripted.Enqueue(entryId => gate.HandleDecisionAsync(
                entryId,
                decision,
                context with { Reason = reason ?? context.Reason },
                amendments));
        }

        /// <param name="beforeThrow">
        /// Optional callback run immediately before the simulated timeout exception is thrown —
        /// used to inject a race (e.g. the restart path transitioning the entry) at exactly the
        /// moment the blocking-timeout path is about to act.
        /// </param>
        public void SimulateTimeout(Func<Task>? beforeThrow = null)
        {
            _simulateTimeout = true;
            _beforeTimeoutThrow = beforeThrow;
        }

        /// <summary>
        /// Never returns a response — <see cref="AwaitEvidenceCardResponseAsync"/> only unblocks
        /// when <paramref name="ct"/> is cancelled, so a real (short) TTL genuinely drives the
        /// <c>CancelAfter</c> window instead of the fake short-circuiting synchronously.
        /// </summary>
        public void HangUntilCancelled() => _hangUntilCancelled = true;

        public bool TryDeliverResponse(Guid docketId, DecisionHandOff response)
        {
            DeliveredHandOffs.Add(response);

            // A live waiter is one that actually receives the response — including whatever the
            // gate attached to it on the way through, which is how the awaiting call learns who
            // the deciding call held the decision to.
            if (HasLiveWaiter)
                _delivered.TrySetResult(response);

            return HasLiveWaiter;
        }

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            SentEvents.Add((connectionId, eventType, payload));
            return Task.CompletedTask;
        }

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            if (eventType == TransportEvent.EvidenceCardRequest)
            {
                EvidenceCardBroadcastAttempts++;
                if (_failNextEvidenceCardBroadcasts > 0)
                {
                    _failNextEvidenceCardBroadcasts--;
                    throw new InvalidOperationException("simulated Evidence Card broadcast failure");
                }
            }

            SentEvents.Add((groupId, eventType, payload));
            return Task.CompletedTask;
        }

        public async Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
        {
            if (_simulateTimeout)
            {
                if (_beforeTimeoutThrow is not null)
                    await _beforeTimeoutThrow();

                // Throw with a fresh cancelled token — not the caller's token — to simulate
                // the internal CTS timeout (distinct from caller cancellation).
                using var timeoutCts = new CancellationTokenSource();
                timeoutCts.Cancel();
                throw new OperationCanceledException("Simulated timeout", timeoutCts.Token);
            }

            if (_hangUntilCancelled)
            {
                // Only ReviewGate's own CancelAfter(options.DefaultDocketTtl) can unblock this —
                // a real, but short, wait exercising the configured TTL end-to-end.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            if (_scripted.TryDequeue(out var decide))
            {
                await decide(docketId);
                if (!_delivered.Task.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "FakeStreamingTransport: the scripted decision was refused, so no hand-off " +
                        "reached the waiter. A refusal is not a decision.");
                }

                return await _delivered.Task;
            }

            // No script: wait for a real TryDeliverResponse, the way a live waiter does.
            if (HasLiveWaiter)
                return await _delivered.Task.WaitAsync(ct);

            throw new InvalidOperationException("FakeStreamingTransport: no scripted decision");
        }
    }



    private sealed class FakeApprovalPolicyEvaluator(ReviewRequirement requirement) : IApprovalPolicyEvaluator
    {
        public Task<ApprovalVerdict> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
            => Task.FromResult<ApprovalVerdict>(requirement);
    }

    /// <summary>Records every log call so a test can assert on level/message without a mocking library.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    /// <summary>
    /// Decorates an <see cref="IDocketStore"/> to cancel <paramref name="cts"/> the instant
    /// <see cref="ConsumeForResubmitAsync"/> wins the claim — modeling a client disconnect
    /// (e.g. a host's resubmit hub RPC threaded with its connection-aborted token, per the d2
    /// evidence pack) landing right after <c>ResubmitAsync</c>'s consume commits but before the
    /// fresh entry finishes filing.
    /// </summary>
    private sealed class CancelOnResubmitConsumeDocketStore(IDocketStore inner, CancellationTokenSource cts) : IDocketStore
    {
        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => inner.SaveContextAsync(sessionId, context, ct);

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => inner.LoadContextAsync(sessionId, ct);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
            => inner.FileDocketEntryAsync(entry, ct);

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => inner.GetDocketEntryAsync(entryId, ct);


        public async Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
        {
            var result = await inner.ConsumeForResubmitAsync(entryId, newEntryId, ct);
            if (result > 0)
                cts.Cancel();
            return result;
        }

        /// <summary>
        /// The claim ResubmitAsync actually races on since the lineage moved onto the row: winning it
        /// is the instant a disconnect has to land to reproduce the orphaned-pointer failure.
        /// </summary>
        public async Task<RecordSupersessionResult> RecordSupersessionAsync(
            Guid entryId, DocketScope scope, Guid supersededBy, CancellationToken ct)
        {
            var result = await inner.RecordSupersessionAsync(entryId, scope, supersededBy, ct);
            if (result is RecordSupersessionResult.Recorded)
                cts.Cancel();
            return result;
        }

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => inner.GetResubmissionParentAsync(entryId, ct);


        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => inner.ListPendingBySessionAsync(sessionId, ct);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => inner.ListAllPendingAsync(ct);

        // ── The scoped, guarded, paged surface ──────────────────────────────
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (ReviewGate gate, FakeStreamingTransport transport, InMemoryDocketStore docketStore)
        CreateGate(
            ReviewRequirement reviewRequirement = ReviewRequirement.ReviewerConfirmation,
            AffiantCoreOptions? options = null,
            TimeProvider? timeProvider = null,
            IDecisionAuthorizationPolicy? authorization = null)
    {
        var transport = new FakeStreamingTransport();
        var store = new InMemoryDocketStore(timeProvider);
        var evaluator = new FakeApprovalPolicyEvaluator(reviewRequirement);
        var gate = new ReviewGate(
            transport, store, evaluator, options ?? new AffiantCoreOptions(),
            NullLogger<ReviewGate>.Instance, timeProvider,
            authorization ?? new AllowAllDecisionAuthorization());
        return (gate, transport, store);
    }

    /// <summary>
    /// The decision context a test that is not about authorization passes: a resolved member
    /// principal in the entries' own tenant. There is no unattributed context to fall back on —
    /// every entry point on the decision surface requires a principal and a tenant (AZ-2).
    /// </summary>
    private static DecisionContext Ctx(
        Principal? principal = null,
        string tenantId = TenantId,
        string? reason = null)
        => new(
            principal ?? new Principal.Member("reviewer-456"),
            tenantId,
            ConversationId: "session-test",
            Channel: "test",
            Reason: reason);

    private static (WriteProposal proposal, ReviewContext context) CreateTestInput(Guid? entryId = null)
    {
        var affidavit = new Affidavit(
            OperationType: "CreateOrder",
            EntityType: "Order",
            EntityId: null,
            Fields: [new AffidavitField("title", "Test Order", null,
                ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "title", 0.8f)))],
            AggregateConfidence: 0.8f,
            PopulatedConfidence: 0.8f,
            EmptyFieldCount: 0,
            Warnings: [],
            RequiresConfirmation: true);

        var proposal = new WriteProposal("CreateOrder", DateTimeOffset.UtcNow, affidavit);
        var context = new ReviewContext(
            SessionId: "session-test",
            TenantId: TenantId,
            UserId: "user-123",
            ReviewerUserId: "reviewer-456",
            Affidavit: affidavit,
            EntryId: entryId);
        return (proposal, context);
    }

    /// <summary>The tenant every fixture in this file files under.</summary>
    private const string TenantId = "tenant-default";

    // ── Tests ─────────────────────────────────────────────────────────────────

    // ── The row a filing writes, and what a replay must not change ────────────

    [Fact]
    public async Task FileForReviewAsync_WritesTheToolAndTheProtocolTagOntoTheRow()
    {
        var (gate, _, store) = CreateGate();
        var (proposal, context) = CreateTestInput(Guid.NewGuid());

        await gate.FileForReviewAsync(proposal, context);

        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);

        // Two later questions need the tool and neither can be answered from the Affidavit: a
        // resubmission re-runs the coverage lookup against the original tool, and an audit of a
        // filed write has to be able to say which tool proposed it.
        Assert.Equal(proposal.ToolName, entry!.ToolName);
        Assert.Equal(AffiantProtocol.Version, entry.ProtocolVersion);

        // A freshly filed row holds no later facts at all.
        Assert.Null(entry.Execution);
        Assert.Null(entry.Decision);
        Assert.Null(entry.Attestation);
        Assert.Null(entry.DecidedAt);
        Assert.Null(entry.AmendedAffidavit);
        Assert.Null(entry.Lineage.Supersedes);
        Assert.Null(entry.Lineage.SupersededBy);
    }

    [Fact]
    public async Task FileForReviewAsync_ReFilingTheSameEntry_ReplaysTheExistingCardAndItsExistingDeadline()
    {
        var clock = new FakeTimeProvider(ClockOrigin);
        var ttl = TimeSpan.FromMinutes(30);
        var (gate, transport, store) = CreateGate(
            ReviewRequirement.ReviewerConfirmation,
            new AffiantCoreOptions { DefaultDocketTtl = ttl },
            clock);
        transport.HasLiveWaiter = false;

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);
        var deadline = (await store.GetDocketEntryAsync(context.EntryId!.Value, default))!.ExpiresAt;

        // The agent retries the same proposal ten minutes later.
        clock.Advance(TimeSpan.FromMinutes(10));
        var replay = await gate.FileForReviewAsync(proposal, context);

        // Never a second entry and never an error — and, above all, never a fresh deadline: a
        // re-file that refreshed it would let a retrying agent hold a card open indefinitely.
        Assert.IsType<ReviewFilingResult.RequiresReview>(replay);
        var afterReplay = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.Equal(deadline, afterReplay!.ExpiresAt);

        var cards = transport.SentEvents
            .Where(e => e.EventType == TransportEvent.EvidenceCardRequest)
            .Select(e => Assert.IsType<EvidenceCardRequest>(e.Payload))
            .ToList();
        Assert.Equal(2, cards.Count);
        Assert.All(cards, card => Assert.Equal(deadline, card.RequiredBy));
    }

    // ── Resubmission: prefilled from what the reviewer corrected, lineage both ways ──

    [Fact]
    public async Task ResubmitAsync_PrefillsFromThePreservedAmendments_AndWritesLineageBothWays()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (_, context) = CreateTestInput();
        var lapsedEntry = CreateLapsedEntry(context);
        await store.FileDocketEntryAsync(lapsedEntry, default);

        // A decision arrives too late, carrying the reviewer's corrections. They are refused as a
        // decision and kept as a fact.
        var corrections = new Dictionary<string, object?> { ["title"] = "Corrected by the reviewer" };
        await gate.HandleDecisionAsync(
            lapsedEntry.EntryId,
            ApprovalDecision.Approved,
            Ctx(),
            corrections,
            CancellationToken.None);

        var filing = await gate.ResubmitAsync(lapsedEntry.EntryId, Ctx());

        var requiresReview = Assert.IsType<ReviewFilingResult.RequiresReview>(filing);
        Assert.NotEqual(lapsedEntry.EntryId, requiresReview.EntryId);

        var superseded = await store.GetDocketEntryAsync(lapsedEntry.EntryId, default);
        var successor = await store.GetDocketEntryAsync(requiresReview.EntryId, default);

        // A resubmission is a NEW entry, never a reopened one: the superseded row keeps its terminal
        // state and records its successor, and the successor records what it replaces, so the
        // history reads forward from either end.
        Assert.Equal(ReviewStatus.Expired, superseded!.Status);
        Assert.Equal(requiresReview.EntryId, superseded.Lineage.SupersededBy);
        Assert.Equal(lapsedEntry.EntryId, successor!.Lineage.Supersedes);
        Assert.Null(successor.Lineage.SupersededBy);

        // The new proposal is prefilled with the reviewer's own correction rather than starting
        // blank and asking them to type it again.
        Assert.NotNull(successor.Amendments);
        Assert.Equal("Corrected by the reviewer", successor.Amendments!["title"]);
    }

    [Fact]
    public async Task ResubmitAsync_ASecondConcurrentResubmission_LosesTheClaim()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (_, context) = CreateTestInput();
        var lapsedEntry = CreateLapsedEntry(context);
        await store.FileDocketEntryAsync(lapsedEntry, default);

        await gate.ResubmitAsync(lapsedEntry.EntryId, Ctx());

        // The claim is one-shot: the successor link is the race guard as well as the lineage.
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ResubmitAsync(lapsedEntry.EntryId, Ctx()));
    }


    [Fact]
    public async Task FileReviewAsync_ReviewerConfirmation_Approved_ReturnsApproved()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueDecision(gate, ApprovalDecision.Approved, Ctx());

        var (proposal, context) = CreateTestInput();
        var outcome = await gate.FileReviewAsync(proposal, context);

        Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
    }

    /// <summary>
    /// AZ-1: the awaiting call writes the row, and the deciding call holds the identity — so the
    /// attestation travels with the response and the row names who agreed either way. A decision
    /// this path could not attribute is one HandleDecisionAsync already refused.
    /// </summary>
    [Fact]
    public async Task FileReviewAsync_ApprovalDeliveredByADecision_WritesTheDecidersAttestation()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var entryId = context.EntryId!.Value;

        // The awaiting half: FileReviewAsync blocks on the transport until a decision arrives.
        transport.HasLiveWaiter = true;
        var awaiting = gate.FileReviewAsync(proposal, context);

        // The deciding half: a real decision, through the gate, from a resolved member principal.
        await gate.HandleDecisionAsync(entryId, ApprovalDecision.Approved, Ctx());

        Assert.IsType<ReviewOutcome.Approved>(await awaiting);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Approved, row!.Status);
        Assert.Equal("reviewer-456", Assert.IsType<Attestor.Member>(row.Attestation!.By).Id);
        Assert.Equal(entryId, row.Attestation.EntryId);
        Assert.Equal(DecisionKind.Approve, row.Decision!.Kind);
    }

    [Fact]
    public async Task FileReviewAsync_StandingOrder_AutoApproves_AndBroadcastsACardThatAsksNobody()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.StandingOrder);

        var (proposal, context) = CreateTestInput();
        var outcome = await gate.FileReviewAsync(proposal, context);

        Assert.IsType<ReviewOutcome.Approved>(outcome);

        // SR-4: a write approved with no person present still appears on a card — the reviewer
        // surface has to be able to see what was approved in the organisation's name — and the card
        // says no confirmation is needed, because nobody is being asked.
        var sent = Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
        Assert.False(Assert.IsType<EvidenceCardRequest>(sent.Payload).RequiresConfirmation);
    }

    [Fact]
    public async Task FileReviewAsync_ClientRejects_ReturnsRejected()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueDecision(gate, ApprovalDecision.Rejected, Ctx(), "Budget exceeded");

        var (proposal, context) = CreateTestInput();
        var outcome = await gate.FileReviewAsync(proposal, context);

        var rejected = Assert.IsType<ReviewOutcome.Rejected>(outcome);
        Assert.Equal("Budget exceeded", rejected.Reason);
    }

    [Fact]
    public async Task FileReviewAsync_Timeout_ReturnsExpired()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.SimulateTimeout();

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var outcome = await gate.FileReviewAsync(proposal, context);

        Assert.IsType<ReviewOutcome.Expired>(outcome);

        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Expired, entry.Status);
    }

    // ── Finding 1a regression: DocketExpired must not broadcast for an entry that ──
    // ── did not actually transition to Expired (blocking-timeout path) ─────────────

    [Fact]
    public async Task FileReviewAsync_ApprovalRacesTimeout_NoExpiredBroadcast_ReturnsApproved()
    {
        var entryId = Guid.NewGuid();
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);

        // Simulate the restart path (HandleDecisionAsync) applying Approved a beat before the
        // blocking-timeout path's own guarded UPDATE runs — the guard must see 0 rows affected.
        transport.SimulateTimeout(beforeThrow: () => store.TransitionAsync(
            entryId, new DocketScope(TenantId), ReviewStatus.Pending,
            new DocketTransitionPatch(
                ReviewStatus.Approved,
                Decision: new DecisionRecord(DecisionKind.Approve, null, DateTimeOffset.UnixEpoch),
                Attestation: new Attestation(
                    Attestor.Member.Of(new Principal.Member("reviewer-456")),
                    DateTimeOffset.UnixEpoch, entryId),
                DecidedAt: DateTimeOffset.UnixEpoch),
            default));

        var (proposal, context) = CreateTestInput(entryId);
        var outcome = await gate.FileReviewAsync(proposal, context);

        // The entry genuinely transitioned to Approved — the timeout path must report that
        // reality, not lie with Expired.
        Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.DoesNotContain(transport.SentEvents, e => e.EventType == TransportEvent.DocketExpired);

        var entry = await store.GetDocketEntryAsync(entryId, default);
        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Approved, entry.Status);
    }

    [Fact]
    public async Task FileReviewAsync_ReferralRequired_FilesPendingAndBlocked_RefusingTheWrite()
    {
        var (gate, _, store) = CreateGate(ReviewRequirement.ReferralRequired);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var outcome = await gate.FileReviewAsync(proposal, context);

        // Referral is a transition no implementation has run, so this version records the level and
        // refuses rather than writing a Deferred status that names semantics nobody has fixed.
        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        // AZ-4: the row is pending and no decision on it will ever be accepted, which is what
        // `decision-not-pending` is registered to mean. The marker's own code and level travel in
        // the detail rather than standing in for the refusal code.
        Assert.Equal(DocketRefusalCodes.DecisionNotPending, refused.Code);
        Assert.Equal(
            $"{DocketRefusalCodes.RequirementNotImplemented}: {nameof(ReviewRequirement.ReferralRequired)}",
            refused.Detail);

        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Pending, entry.Status);
        var blocked = Assert.IsType<BlockedMarker.RequirementNotImplemented>(entry.Blocked);
        Assert.Equal(ReviewRequirement.ReferralRequired, blocked.Level);
    }

    [Fact]
    public async Task FileReviewAsync_BlockedEntry_RefusesEveryDecisionOnIt()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.MultiParty);
        transport.HasLiveWaiter = false;

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);

        var (outcome, _) = await gate.HandleDecisionAsync(
            context.EntryId!.Value, ApprovalDecision.Approved, Ctx());

        // A blocked entry never accepts a decision, and the refusal names the code that blocked it
        // rather than a bare "not pending" a host cannot act on.
        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionNotPending, refused.Code);
        Assert.Equal(
            $"{DocketRefusalCodes.RequirementNotImplemented}: {nameof(ReviewRequirement.MultiParty)}",
            refused.Detail);

        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.Equal(ReviewStatus.Pending, entry!.Status);
        Assert.Null(entry.Execution);
    }

    [Fact]
    public async Task FileReviewAsync_DoubleSubmit_Idempotent_SingleEntry()
    {
        var entryId = Guid.NewGuid();
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueDecision(gate, ApprovalDecision.Approved, Ctx());

        var (proposal, context) = CreateTestInput(entryId);

        var outcome1 = await gate.FileReviewAsync(proposal, context);
        // Second call: entry is already Approved, should return immediately without re-filing.
        var outcome2 = await gate.FileReviewAsync(proposal, context);

        Assert.IsType<ReviewOutcome.Approved>(outcome1);
        Assert.IsType<ReviewOutcome.Approved>(outcome2);

        // Only one entry should exist.
        var entry = await store.GetDocketEntryAsync(entryId, default);
        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Approved, entry.Status);
    }

    [Fact]
    public async Task FileReviewAsync_CancelledToken_ThrowsWithoutFilingEntry()
    {
        var entryId = Guid.NewGuid();
        var (gate, _, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);

        var (proposal, context) = CreateTestInput(entryId);
        var cancelledToken = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gate.FileReviewAsync(proposal, context, cancelledToken));

        var entry = await store.GetDocketEntryAsync(entryId, default);
        Assert.Null(entry);
    }

    [Fact]
    public async Task FileReviewAsync_MultiParty_IsBlockedNotDegradedToOneReviewer()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.MultiParty);
        transport.EnqueueDecision(gate, ApprovalDecision.Approved, Ctx());

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var outcome = await gate.FileReviewAsync(proposal, context);

        // The failure this rule exists to prevent: a write needing several parties' joint approval
        // used to fall through to the one-reviewer branch and be satisfied by a single click.
        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionNotPending, refused.Code);
        Assert.Equal(
            $"{DocketRefusalCodes.RequirementNotImplemented}: {nameof(ReviewRequirement.MultiParty)}",
            refused.Detail);

        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.Equal(ReviewStatus.Pending, entry!.Status);
        Assert.IsType<BlockedMarker.RequirementNotImplemented>(entry.Blocked);

        // The card still goes out — a blocked entry's card says so on its face rather than vanishing.
        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
    }

    // ── Amendment round-trip (issue #6) ──────────────────────────────────────

    [Fact]
    public async Task FileReviewAsync_ApprovedWithAmendments_PersistsAmendmentsOnDocketEntry()
    {
        var entryId = Guid.NewGuid();
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var amendments = new Dictionary<string, object?>
        {
            ["title"] = "Reviewer-Edited Title",
            ["notes"] = null
        };
        transport.EnqueueDecision(gate, ApprovalDecision.Approved, Ctx(), amendments: amendments);

        var (proposal, context) = CreateTestInput(entryId);
        var outcome = await gate.FileReviewAsync(proposal, context);

        Assert.IsType<ReviewOutcome.Approved>(outcome);

        var entry = await store.GetDocketEntryAsync(entryId, default);
        Assert.NotNull(entry);
        Assert.NotNull(entry.Amendments);
        Assert.Equal("Reviewer-Edited Title", entry.Amendments!["title"]);
        Assert.True(entry.Amendments.ContainsKey("notes"));
        Assert.Null(entry.Amendments["notes"]);
    }

    // ── The amended Affidavit that travels beside the proposal ────────────────

    [Fact]
    public async Task FileReviewAsync_ApprovedWithAmendments_ReturnsTheAmendedAffidavitBesideTheProposal()
    {
        var entryId = Guid.NewGuid();
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueDecision(
            gate, ApprovalDecision.Approved, Ctx(),
            amendments: new Dictionary<string, object?> { ["title"] = "Reviewer-Edited Title" });

        var (proposal, context) = CreateTestInput(entryId);
        var outcome = await gate.FileReviewAsync(proposal, context);

        var approved = Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.NotNull(approved.AmendedAffidavit);

        // The reviewer's correction, their act on top of the field's chain, and the recomputed
        // numbers — the machine had proposed the title at 0.8.
        var field = Assert.Single(approved.AmendedAffidavit!.Fields);
        Assert.Equal("Reviewer-Edited Title", field.Value);
        Assert.Equal(ProvenanceSource.UserStated, field.Provenance.Current.Source);
        Assert.IsType<ProvenanceBinding.ReviewerAct>(field.Provenance.Current.Binding);
        Assert.Equal(1.0f, approved.AmendedAffidavit.AggregateConfidence, 5);

        // The filed proposal is untouched: the card the reviewer was shown is still readable.
        var entry = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(0.8f, entry!.Envelope.AggregateConfidence, 5);
        Assert.Equal("Test Order", entry.Envelope.Fields.Single().Value);
    }

    [Fact]
    public async Task FileReviewAsync_ApprovedUnchanged_CarriesNoAmendedAffidavit()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueDecision(gate, ApprovalDecision.Approved, Ctx());

        var (proposal, context) = CreateTestInput();
        var outcome = await gate.FileReviewAsync(proposal, context);

        Assert.Null(Assert.IsType<ReviewOutcome.Approved>(outcome).AmendedAffidavit);
    }

    [Fact]
    public async Task HandleDecisionAsync_ApprovedWithAmendments_ReturnsTheAmendedAffidavit()
    {
        // The restart path: no live waiter, so the decision is replayed through the docket store.
        var entryId = Guid.NewGuid();
        var (gate, _, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);

        var (proposal, context) = CreateTestInput(entryId);
        await gate.FileForReviewAsync(proposal, context);

        var (outcome, _) = await gate.HandleDecisionAsync(
            entryId,
            ApprovalDecision.Approved,
            Ctx(),
            new Dictionary<string, object?> { ["title"] = "Reviewer-Edited Title" });

        var approved = Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.Equal("Reviewer-Edited Title", approved.AmendedAffidavit!.Fields.Single().Value);
        Assert.Equal(1.0f, approved.AmendedAffidavit.AggregateConfidence, 5);
        Assert.Equal(0.8f, (await store.GetDocketEntryAsync(entryId, default))!.Envelope.AggregateConfidence, 5);
    }

    [Fact]
    public async Task FileReviewAsync_AmendmentNamingAFieldTheAffidavitDoesNotPropose_StillApproves()
    {
        // A host surface that offered an edit for a field the write never proposed is a defect, but
        // it must not undo a decision that has already transitioned: the amendments are persisted,
        // the approval stands, and only the amended record is withheld.
        var entryId = Guid.NewGuid();
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueDecision(
            gate, ApprovalDecision.Approved, Ctx(),
            amendments: new Dictionary<string, object?> { ["notes"] = "not a proposed field" });

        var (proposal, context) = CreateTestInput(entryId);
        var outcome = await gate.FileReviewAsync(proposal, context);

        var approved = Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.Null(approved.AmendedAffidavit);

        var entry = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Approved, entry!.Status);
        Assert.True(entry.Amendments!.ContainsKey("notes"));
    }

    [Fact]
    public async Task FileReviewAsync_ApprovedWithoutAmendments_LeavesAmendmentsNull()
    {
        var entryId = Guid.NewGuid();
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueDecision(gate, ApprovalDecision.Approved, Ctx());

        var (proposal, context) = CreateTestInput(entryId);
        await gate.FileReviewAsync(proposal, context);

        var entry = await store.GetDocketEntryAsync(entryId, default);
        Assert.NotNull(entry);
        Assert.Null(entry.Amendments);
    }

    /// <summary>
    /// A waiter is handed the <em>result</em> of the decision, never the decision itself: the row is
    /// already written and attested by the time a hand-off exists, so the awaiting call reports it
    /// and writes nothing (AZ-1, AZ-2).
    /// </summary>
    [Fact]
    public async Task HandleDecisionAsync_LiveWaiter_HandsOverTheSettledOutcomeAndItsAttestation()
    {
        var entryId = Guid.NewGuid();
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.HasLiveWaiter = true;

        var (proposal, context) = CreateTestInput(entryId);
        await gate.FileForReviewAsync(proposal, context);

        var (outcome, createdAt) = await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, Ctx(),
            new Dictionary<string, object?> { ["title"] = "Reviewer-Edited Title" });

        // The deciding call owns the outcome, waiter or no waiter.
        Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.NotNull(createdAt);

        var delivered = Assert.Single(transport.DeliveredHandOffs);
        Assert.Equal(entryId, delivered.EntryId);
        Assert.Equal(ApprovalDecision.Approved, delivered.Decision);
        Assert.Equal("reviewer-456", Assert.IsType<Attestor.Member>(delivered.Attestation.By).Id);
        Assert.IsType<ReviewOutcome.Approved>(delivered.Outcome);

        var row = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal("Reviewer-Edited Title", row!.Amendments!["title"]);
    }

    [Fact]
    public async Task HandleDecisionAsync_RestartPath_PersistsAmendmentsAfterApproval()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false; // No FileReviewAsync task is awaiting — restart path.

        var (_, context) = CreateTestInput();
        var entry = new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: context.SessionId,
            TenantId: context.TenantId,
            UserId: context.UserId,
            ReviewerUserId: context.ReviewerUserId,
            OperationType: "CreateOrder",
            Envelope: context.Affidavit,
            Status: ReviewStatus.Pending,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
            Amendments: null);
        await store.FileDocketEntryAsync(entry, default);

        var amendments = new Dictionary<string, object?> { ["title"] = "Restart-Path Edit" };
        var (outcome, createdAt) = await gate.HandleDecisionAsync(
            entry.EntryId, ApprovalDecision.Approved, Ctx(),
            amendments);

        Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.Equal(entry.CreatedAt, createdAt);

        var updated = await store.GetDocketEntryAsync(entry.EntryId, default);
        Assert.NotNull(updated);
        Assert.Equal(ReviewStatus.Approved, updated.Status);
        Assert.NotNull(updated.Amendments);
        Assert.Equal("Restart-Path Edit", updated.Amendments!["title"]);
    }

    [Fact]
    public async Task HandleDecisionAsync_RestartPath_RejectedIgnoresAmendments()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (_, context) = CreateTestInput();
        var entry = new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: context.SessionId,
            TenantId: context.TenantId,
            UserId: context.UserId,
            ReviewerUserId: context.ReviewerUserId,
            OperationType: "CreateOrder",
            Envelope: context.Affidavit,
            Status: ReviewStatus.Pending,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
            Amendments: null);
        await store.FileDocketEntryAsync(entry, default);

        var amendments = new Dictionary<string, object?> { ["title"] = "Should Not Persist" };
        var (outcome, _) = await gate.HandleDecisionAsync(
            entry.EntryId, ApprovalDecision.Rejected, Ctx(),
            amendments);

        Assert.IsType<ReviewOutcome.Rejected>(outcome);

        var updated = await store.GetDocketEntryAsync(entry.EntryId, default);
        Assert.NotNull(updated);
        Assert.Equal(ReviewStatus.Rejected, updated.Status);
        Assert.Null(updated.Amendments);
    }

    // ── D2: TTL option becomes real (issue #7 / F0-A2) ───────────────────────

    [Fact]
    public async Task FileReviewAsync_DefaultDocketTtlOption_DrivesExpiresAtStamp()
    {
        async Task<DateTimeOffset> FileWithTtl(TimeSpan ttl)
        {
            var (gate, transport, store) = CreateGate(
                ReviewRequirement.ReviewerConfirmation,
                new AffiantCoreOptions { DefaultDocketTtl = ttl });
            transport.EnqueueDecision(gate, ApprovalDecision.Approved, Ctx());
            var (proposal, context) = CreateTestInput(Guid.NewGuid());
            await gate.FileReviewAsync(proposal, context);
            var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
            return entry!.ExpiresAt;
        }

        var shortExpiry = await FileWithTtl(TimeSpan.FromSeconds(5));
        // Deliberately exceeds the deleted 10-minute constant — proves ExpiresAt tracks the
        // configured option rather than a hardcoded value.
        var longExpiry = await FileWithTtl(TimeSpan.FromMinutes(45));

        var delta = longExpiry - shortExpiry;
        Assert.True(
            delta > TimeSpan.FromMinutes(40),
            $"expected ExpiresAt to track the configured DefaultDocketTtl option, delta was {delta}");
    }

    [Fact]
    public async Task FileReviewAsync_ShortConfiguredTtl_UnblocksAwaitWindowQuickly()
    {
        var (gate, transport, _) = CreateGate(
            ReviewRequirement.ReviewerConfirmation,
            new AffiantCoreOptions { DefaultDocketTtl = TimeSpan.FromMilliseconds(50) });
        transport.HangUntilCancelled(); // only the internal CancelAfter(options.DefaultDocketTtl) unblocks this

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await gate.FileReviewAsync(proposal, context);
        sw.Stop();

        Assert.IsType<ReviewOutcome.Expired>(outcome);
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(5),
            $"expected the configured 50ms TTL to unblock the await quickly, took {sw.Elapsed}");

        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.DocketExpired);
    }

    // ── D1 regression: FileForReviewAsync files no waiter (issue affiant-host-apps#25 / F0-A1) ──

    [Fact]
    public async Task FileForReviewAsync_NoWaiterRegistered_HandleDecisionAsync_SucceedsViaRestartPath()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.HasLiveWaiter = false; // FileForReviewAsync never calls AwaitEvidenceCardResponseAsync — no waiter exists

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var filing = await gate.FileForReviewAsync(proposal, context);

        var requiresReview = Assert.IsType<ReviewFilingResult.RequiresReview>(filing);
        Assert.Equal(context.EntryId!.Value, requiresReview.EntryId);
        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);

        // Immediately deliver a decision — as if the host received it right after filing,
        // long before any FileReviewAsync-style blocking await could exist.
        var amendments = new Dictionary<string, object?> { ["title"] = "A1 regression edit" };
        var (outcome, createdAt) = await gate.HandleDecisionAsync(
            requiresReview.EntryId, ApprovalDecision.Approved, Ctx(),
            amendments);

        Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.NotNull(createdAt);

        var entry = await store.GetDocketEntryAsync(requiresReview.EntryId, default);
        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Approved, entry.Status);
        Assert.NotNull(entry.Amendments);
        Assert.Equal("A1 regression edit", entry.Amendments!["title"]);
    }

    [Fact]
    public async Task FileForReviewAsync_StandingOrder_ReturnsDecided_AndBroadcastsACardThatAsksNobody()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.StandingOrder);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var filing = await gate.FileForReviewAsync(proposal, context);

        var decided = Assert.IsType<ReviewFilingResult.Decided>(filing);
        Assert.IsType<ReviewOutcome.Approved>(decided.Outcome);

        var sent = Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
        Assert.False(Assert.IsType<EvidenceCardRequest>(sent.Payload).RequiresConfirmation);

        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Approved, entry.Status);
    }

    // ── D3: persist late amendments (issue #8 / F0-A3) ───────────────────────

    [Fact]
    public async Task HandleDecisionAsync_LateAmendmentsOnExpiredEntry_PersistedAndFlagSet()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (_, context) = CreateTestInput();
        var expiredEntry = new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: context.SessionId,
            TenantId: context.TenantId,
            UserId: context.UserId,
            ReviewerUserId: context.ReviewerUserId,
            OperationType: "CreateOrder",
            Envelope: context.Affidavit,
            Status: ReviewStatus.Pending,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-15),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            Amendments: null);
        await FileExpiredAsync(store, expiredEntry);

        var lateAmendments = new Dictionary<string, object?> { ["title"] = "Late reviewer edit" };
        var (outcome, createdAt) = await gate.HandleDecisionAsync(
            expiredEntry.EntryId,
            ApprovalDecision.Approved,
            Ctx(),
            lateAmendments,
            CancellationToken.None);

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionExpired, refused.Code);
        Assert.Equal("amendments-preserved", refused.Detail);
        Assert.Equal(expiredEntry.CreatedAt, createdAt);

        var updated = await store.GetDocketEntryAsync(expiredEntry.EntryId, default);
        Assert.NotNull(updated);
        Assert.Equal(ReviewStatus.Expired, updated.Status); // status itself is not resurrected here

        // The corrections live under their own fact, with the act that carried them — not under
        // Amendments, which is what an approval ACCEPTED. Nobody accepted these.
        Assert.Null(updated.Amendments);
        Assert.NotNull(updated.PreservedAmendments);
        Assert.Equal("Late reviewer edit", updated.PreservedAmendments!.Amendments["title"]);
        Assert.Equal("reviewer-456", updated.PreservedAmendments.By);

        // The instant is the gate's own reading, not the caller's claim (AZ-1): the record says
        // when the implementation observed the act.
        Assert.InRange(
            updated.PreservedAmendments.At,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task HandleDecisionAsync_NotFoundEntry_IsRefusedAsNotFound()
    {
        var (gate, transport, _) = CreateGate();
        transport.HasLiveWaiter = false;

        var (outcome, createdAt) = await gate.HandleDecisionAsync(
            Guid.NewGuid(), ApprovalDecision.Approved, Ctx(),
            new Dictionary<string, object?> { ["x"] = 1 });

        // Not "expired": nothing ran out of time, the entry does not exist. Reporting the two the
        // same way told a host a deadline had passed on a row it had never filed.
        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.EntryNotFound, refused.Code);
        Assert.Null(createdAt);
    }

    [Fact]
    public async Task HandleDecisionAsync_EntryInAnotherTenant_IsNotFound_NotForbidden()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);

        var (outcome, _) = await gate.HandleDecisionAsync(
            context.EntryId!.Value,
            ApprovalDecision.Approved,
            Ctx(tenantId: "some-other-tenant"));

        // Telling a caller that an id they may not touch exists is the leak the tenant check closes.
        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.EntryNotFound, refused.Code);

        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.Equal(ReviewStatus.Pending, entry!.Status);
    }

    [Fact]
    public async Task HandleDecisionAsync_SecondDecisionOnADecidedEntry_IsRefusedAsNotPending()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);

        var (first, _) = await gate.HandleDecisionAsync(
            context.EntryId!.Value, ApprovalDecision.Approved, Ctx());
        Assert.IsType<ReviewOutcome.Approved>(first);

        var (second, _) = await gate.HandleDecisionAsync(
            context.EntryId!.Value, ApprovalDecision.Rejected, Ctx());

        // The row is already decided, so the second decision is refused rather than applied on top —
        // and it is told which of the two "not pending" refusals it got.
        var refused = Assert.IsType<ReviewOutcome.Refused>(second);
        Assert.Equal(DocketRefusalCodes.DecisionNotPending, refused.Code);

        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.Equal(ReviewStatus.Approved, entry!.Status);
        Assert.Equal(ExecutionOutcome.Unexecuted, entry.Execution);
        Assert.NotNull(entry.Decision);
        Assert.Equal(DecisionKind.Approve, entry.Decision!.Kind);
        Assert.NotNull(entry.DecidedAt);
    }

    // ── affiant#14: restart path persists Expired + broadcasts on TTL lapse ──

    private DocketEntry CreateLapsedEntry(ReviewContext context, Guid? entryId = null) => new(
        EntryId: entryId ?? Guid.NewGuid(),
        SessionId: context.SessionId,
        TenantId: context.TenantId,
        UserId: context.UserId,
        ReviewerUserId: context.ReviewerUserId,
        OperationType: "CreateOrder",
        Envelope: context.Affidavit,
        Status: ReviewStatus.Pending,
        CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-15),
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), // lapsed — the sweep has not run yet
        Amendments: null);

    [Fact]
    public async Task HandleDecisionAsync_LapsedTtlNotYetSwept_PersistsExpired_AndBroadcastsDocketExpired()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (_, context) = CreateTestInput();
        var lapsedEntry = CreateLapsedEntry(context);
        await store.FileDocketEntryAsync(lapsedEntry, default);

        var (outcome, createdAt) = await gate.HandleDecisionAsync(
            lapsedEntry.EntryId, ApprovalDecision.Approved, Ctx());

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionExpired, refused.Code);
        Assert.Null(refused.Detail); // nothing to preserve — this decision carried no amendments
        Assert.Equal(lapsedEntry.CreatedAt, createdAt);

        // Durably persisted — not the pre-fix Pending steady state that lasted up to 30s.
        var updated = await store.GetDocketEntryAsync(lapsedEntry.EntryId, default);
        Assert.NotNull(updated);
        Assert.Equal(ReviewStatus.Expired, updated.Status);

        var broadcast = Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.DocketExpired);
        var notification = Assert.IsType<DocketExpiredNotification>(broadcast.Payload);
        Assert.Equal(lapsedEntry.EntryId, notification.DocketId);
    }

    [Fact]
    public async Task HandleDecisionAsync_LapsedTtl_ResubmitAsyncSucceedsImmediately_NoSweepNeeded()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (_, context) = CreateTestInput();
        var lapsedEntry = CreateLapsedEntry(context);
        await store.FileDocketEntryAsync(lapsedEntry, default);

        await gate.HandleDecisionAsync(
            lapsedEntry.EntryId, ApprovalDecision.Rejected, Ctx());

        // No DocketExpiryService sweep runs in this test — ResubmitAsync must not need one.
        var filing = await gate.ResubmitAsync(lapsedEntry.EntryId, Ctx());

        var requiresReview = Assert.IsType<ReviewFilingResult.RequiresReview>(filing);
        Assert.NotEqual(lapsedEntry.EntryId, requiresReview.EntryId);
    }

    [Fact]
    public async Task HandleDecisionAsync_RepeatedLateDecisions_Idempotent_NoSecondBroadcast_NoError()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (_, context) = CreateTestInput();
        var lapsedEntry = CreateLapsedEntry(context);
        await store.FileDocketEntryAsync(lapsedEntry, default);

        var (firstOutcome, _) = await gate.HandleDecisionAsync(
            lapsedEntry.EntryId, ApprovalDecision.Approved, Ctx());
        var (secondOutcome, _) = await gate.HandleDecisionAsync(
            lapsedEntry.EntryId, ApprovalDecision.Approved, Ctx());

        Assert.Equal(
            DocketRefusalCodes.DecisionExpired,
            Assert.IsType<ReviewOutcome.Refused>(firstOutcome).Code);
        Assert.Equal(
            DocketRefusalCodes.DecisionExpired,
            Assert.IsType<ReviewOutcome.Refused>(secondOutcome).Code);

        // Exactly one DocketExpired broadcast across both calls — the second (losing) call must
        // not re-broadcast just because the entry is still genuinely Expired when it re-reads.
        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.DocketExpired);

        var updated = await store.GetDocketEntryAsync(lapsedEntry.EntryId, default);
        Assert.NotNull(updated);
        Assert.Equal(ReviewStatus.Expired, updated.Status);
    }

    [Fact]
    public async Task HandleDecisionAsync_LapsedTtlWithAmendments_PersistsBothStatusAndAmendments()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (_, context) = CreateTestInput();
        var lapsedEntry = CreateLapsedEntry(context);
        await store.FileDocketEntryAsync(lapsedEntry, default);

        var amendments = new Dictionary<string, object?> { ["title"] = "Late reviewer edit" };
        var (outcome, _) = await gate.HandleDecisionAsync(
            lapsedEntry.EntryId,
            ApprovalDecision.Approved,
            Ctx(),
            amendments,
            CancellationToken.None);

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionExpired, refused.Code);
        Assert.Equal("amendments-preserved", refused.Detail);

        var updated = await store.GetDocketEntryAsync(lapsedEntry.EntryId, default);
        Assert.NotNull(updated);
        Assert.Equal(ReviewStatus.Expired, updated.Status);
        Assert.NotNull(updated.PreservedAmendments);
        Assert.Equal("Late reviewer edit", updated.PreservedAmendments!.Amendments["title"]);
    }

    /// <summary>
    /// AZ-3, PV-3: a machine caller with nothing to relay cannot attest a decision, so it cannot
    /// leave a correction on a row either — a resubmission prefills preserved values as a
    /// <em>person's</em> correction, and a record that cannot say whose correction it is would put
    /// words in a person's mouth. The refusal comes before the row is read at all.
    /// </summary>
    [Fact]
    public async Task HandleDecisionAsync_LateDecisionFromAMachineCaller_PreservesNothing()
    {
        var (gate, transport, store) = CreateGate();
        transport.HasLiveWaiter = false;

        var (_, context) = CreateTestInput();
        var lapsedEntry = CreateLapsedEntry(context);
        await store.FileDocketEntryAsync(lapsedEntry, default);

        var amendments = new Dictionary<string, object?> { ["title"] = "Late reviewer edit" };
        var (outcome, _) = await gate.HandleDecisionAsync(
            lapsedEntry.EntryId,
            ApprovalDecision.Approved,
            Ctx(principal: new Principal.Service("batch-runner")),
            amendments);

        var refused = Assert.IsType<ReviewOutcome.Refused>(outcome);
        Assert.Equal(DocketRefusalCodes.DecisionUnauthorized, refused.Code);

        var updated = await store.GetDocketEntryAsync(lapsedEntry.EntryId, default);
        Assert.Null(updated!.PreservedAmendments);
    }

    // ── P1a: Evidence Card broadcast retry (affiant#22 / FV-9) ───────────────

    private static ActivityListener FrameworkListener() => new()
    {
        // Hardcoded name, not AffiantTelemetry.AffiantActivitySource.Name — see the identical
        // comment in ReviewGateFilterTests.FrameworkListener for why (cctor re-entrancy hazard).
        ShouldListenTo = source => source.Name == "Affiant.Framework",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    [Fact]
    public async Task FileForReviewAsync_BroadcastFailsOnce_RetrySucceeds_StillReportsRequiresReview()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.FailNextEvidenceCardBroadcasts(1);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var filing = await gate.FileForReviewAsync(proposal, context);

        var requiresReview = Assert.IsType<ReviewFilingResult.RequiresReview>(filing);
        Assert.Equal(context.EntryId!.Value, requiresReview.EntryId);
        Assert.Equal(2, transport.EvidenceCardBroadcastAttempts); // failed once, retried once, succeeded
        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
        Assert.DoesNotContain(transport.SentEvents, e => e.EventType == TransportEvent.SystemNotification);

        // The entry is durably filed regardless of the broadcast hiccup.
        var entry = await store.GetDocketEntryAsync(requiresReview.EntryId, default);
        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Pending, entry.Status);
    }

    [Fact]
    public async Task FileForReviewAsync_BroadcastFailsTwice_StillReportsRequiresReview_NeverLies()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.FailNextEvidenceCardBroadcasts(2);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var filing = await gate.FileForReviewAsync(proposal, context);

        // Filing must still report success — the proposal genuinely IS filed and Pending.
        var requiresReview = Assert.IsType<ReviewFilingResult.RequiresReview>(filing);
        Assert.Equal(2, transport.EvidenceCardBroadcastAttempts); // exactly one retry, no more
        Assert.DoesNotContain(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);

        var entry = await store.GetDocketEntryAsync(requiresReview.EntryId, default);
        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Pending, entry.Status);
    }

    [Fact]
    public async Task FileForReviewAsync_BroadcastFailsTwice_EmitsOTelEvent_ObservedViaRealActivityListener()
    {
        using var listener = FrameworkListener();
        ActivitySource.AddActivityListener(listener);
        using var span = AffiantTelemetry.AffiantActivitySource.StartActivity("invoke_agent");
        Assert.NotNull(span);

        var (gate, transport, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.FailNextEvidenceCardBroadcasts(2);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);

        var evt = Assert.Single(span!.Events, e => e.Name == "affiant.review.broadcast_failed");
        var tags = evt.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(context.EntryId!.Value.ToString(), tags["docket.entry_id"]);
        Assert.Equal("InvalidOperationException", tags["exception.type"]);
    }

    [Fact]
    public async Task FileForReviewAsync_BroadcastFailsTwice_BestEffortSystemNotificationSent()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.FailNextEvidenceCardBroadcasts(2);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);

        var notification = Assert.Single(
            transport.SentEvents, e => e.EventType == TransportEvent.SystemNotification);

        // P1b: ReviewGate's own call site migrated from an anonymous { level, message } object to
        // the named SystemNotificationPayload record — same wire shape, now a real type.
        var payload = Assert.IsType<SystemNotificationPayload>(notification.Payload);
        Assert.Equal("warning", payload.Level);
        Assert.Contains("reviewers were", payload.Message);
    }

    // ── D4: ResubmitAsync (framework half of issue #9) ───────────────────────

    [Fact]
    public async Task ResubmitAsync_ExpiredEntry_FilesFreshPendingEntry_WithPriorAmendmentsBroadcast()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var priorAmendments = new Dictionary<string, object?> { ["title"] = "Edited before expiry" };
        var expiredEntry = new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: "session-test",
            TenantId: TenantId,
            UserId: "user-123",
            ReviewerUserId: "reviewer-456",
            OperationType: "CreateOrder",
            Envelope: CreateTestInput().context.Affidavit,
            Status: ReviewStatus.Pending,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-20),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            Amendments: priorAmendments);
        await FileExpiredAsync(store, expiredEntry);

        var filing = await gate.ResubmitAsync(expiredEntry.EntryId, Ctx());

        var requiresReview = Assert.IsType<ReviewFilingResult.RequiresReview>(filing);
        Assert.NotEqual(expiredEntry.EntryId, requiresReview.EntryId); // fresh id

        var freshEntry = await store.GetDocketEntryAsync(requiresReview.EntryId, default);
        Assert.NotNull(freshEntry);
        Assert.Equal(ReviewStatus.Pending, freshEntry.Status);
        Assert.True(freshEntry.ExpiresAt > DateTimeOffset.UtcNow); // fresh TTL, not the original's already-past expiry

        var broadcast = Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
        var request = Assert.IsType<EvidenceCardRequest>(broadcast.Payload);
        Assert.Equal(requiresReview.EntryId, request.DocketId);
        Assert.NotNull(request.PriorAmendments);
        Assert.Equal("Edited before expiry", request.PriorAmendments!["title"]);
    }

    // ── Refuter regression: the orphan-pointer log must fire on cancellation too ──

    [Fact]
    public async Task ResubmitAsync_FilingCancelledAfterConsumeWon_LogsErrorWithBothEntryIds_AndRethrows()
    {
        var transport = new FakeStreamingTransport();
        var innerStore = new InMemoryDocketStore();
        var evaluator = new FakeApprovalPolicyEvaluator(ReviewRequirement.ReviewerConfirmation);
        var capturingLogger = new CapturingLogger<ReviewGate>();
        using var cts = new CancellationTokenSource();
        var store = new CancelOnResubmitConsumeDocketStore(innerStore, cts);
        var gate = new ReviewGate(
            transport, store, evaluator, new AffiantCoreOptions(), capturingLogger,
            timeProvider: null, new AllowAllDecisionAuthorization());

        var expiredEntry = new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: "session-test",
            TenantId: TenantId,
            UserId: "user-123",
            ReviewerUserId: "reviewer-456",
            OperationType: "CreateOrder",
            Envelope: CreateTestInput().context.Affidavit,
            Status: ReviewStatus.Pending,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-20),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            Amendments: null);
        await FileExpiredAsync(innerStore, expiredEntry);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gate.ResubmitAsync(expiredEntry.EntryId, Ctx(), cts.Token));

        // The consume already won and committed ResubmittedTo before cancellation landed — the
        // documented orphaned-pointer trade-off (see ResubmitAsync's remarks).
        var reread = await innerStore.GetDocketEntryAsync(expiredEntry.EntryId, default);
        Assert.NotNull(reread!.ResubmittedTo);

        var errorEntry = Assert.Single(capturingLogger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(expiredEntry.EntryId.ToString(), errorEntry.Message);
        Assert.Contains(reread.ResubmittedTo.Value.ToString(), errorEntry.Message);
    }

    [Fact]
    public async Task ResubmitAsync_NonExpiredEntry_ThrowsInvalidOperationException()
    {
        var (gate, _, store) = CreateGate();
        var pendingEntry = new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: "session-test",
            TenantId: TenantId,
            UserId: "user-123",
            ReviewerUserId: "reviewer-456",
            OperationType: "CreateOrder",
            Envelope: CreateTestInput().context.Affidavit,
            Status: ReviewStatus.Pending,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
            Amendments: null);
        await store.FileDocketEntryAsync(pendingEntry, default);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.ResubmitAsync(pendingEntry.EntryId, Ctx()));
    }

    [Fact]
    public async Task ResubmitAsync_UnknownEntry_IsRefusedAsNotFound()
    {
        var (gate, _, _) = CreateGate();

        var filing = await gate.ResubmitAsync(Guid.NewGuid(), Ctx());

        // The same answer a caller in another tenant gets, and for the same reason (AZ-2).
        var decided = Assert.IsType<ReviewFilingResult.Decided>(filing);
        var refused = Assert.IsType<ReviewOutcome.Refused>(decided.Outcome);
        Assert.Equal(DocketRefusalCodes.EntryNotFound, refused.Code);
    }

    // ── D2: double-resubmit race guard (affiant#31) ───────────────────────────

    [Fact]
    public async Task ResubmitAsync_GenuinelyConcurrentCallsOnSameExpiredEntry_ExactlyOneSucceeds_ExactlyOneNewPendingEntry()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var expiredEntry = new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: "session-test",
            TenantId: TenantId,
            UserId: "user-123",
            ReviewerUserId: "reviewer-456",
            OperationType: "CreateOrder",
            Envelope: CreateTestInput().context.Affidavit,
            Status: ReviewStatus.Pending,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-20),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            Amendments: null);
        await FileExpiredAsync(store, expiredEntry);

        async Task<ReviewFilingResult?> TryResubmitAsync()
        {
            try { return await gate.ResubmitAsync(expiredEntry.EntryId, Ctx()); }
            catch (InvalidOperationException) { return null; }
        }

        // Task.Run (not a sequential simulation) so both calls genuinely race the InMemoryDocketStore's
        // lock inside ConsumeForResubmitAsync — the concurrency pack's own named gap (zero
        // Task.WhenAll usage anywhere in the store test suite).
        var first = Task.Run(TryResubmitAsync);
        var second = Task.Run(TryResubmitAsync);
        var results = await Task.WhenAll(first, second);

        var successes = results.Where(r => r is not null).ToList();
        var winner = Assert.Single(successes);
        var requiresReview = Assert.IsType<ReviewFilingResult.RequiresReview>(winner);
        Assert.NotEqual(expiredEntry.EntryId, requiresReview.EntryId);

        // The loser threw before ever filing or broadcasting — only one Evidence Card exists.
        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);

        var sourceAfter = await store.GetDocketEntryAsync(expiredEntry.EntryId, default);
        Assert.NotNull(sourceAfter);
        Assert.Equal(ReviewStatus.Expired, sourceAfter.Status); // never resurrected — no ReviewStatus.Resubmitted
        Assert.Equal(requiresReview.EntryId, sourceAfter.ResubmittedTo);

        var newEntry = await store.GetDocketEntryAsync(requiresReview.EntryId, default);
        Assert.NotNull(newEntry);
        Assert.Equal(ReviewStatus.Pending, newEntry.Status);
    }

    // ── D3: RebroadcastPendingCardsAsync reconnect primitive (Area-5 Decision 3 criterion 2, affiant#28) ──

    [Fact]
    public async Task RebroadcastPendingCardsAsync_PendingEntryInSession_BroadcastsEvidenceCardForIt()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var (proposal, context) = CreateTestInput(Guid.NewGuid());

        var filing = await gate.FileForReviewAsync(proposal, context);
        var entryId = Assert.IsType<ReviewFilingResult.RequiresReview>(filing).EntryId;
        transport.SentEvents.Clear(); // drop the filing-time broadcast — only the rebroadcast counts here

        await gate.RebroadcastPendingCardsAsync(context.SessionId, TenantId, CancellationToken.None);

        var broadcast = Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
        Assert.Equal(context.SessionId, broadcast.GroupId);
        var request = Assert.IsType<EvidenceCardRequest>(broadcast.Payload);
        Assert.Equal(entryId, request.DocketId);
    }

    [Fact]
    public async Task RebroadcastPendingCardsAsync_NoPendingEntriesInSession_NoBroadcast()
    {
        var (gate, transport, _) = CreateGate();

        await gate.RebroadcastPendingCardsAsync("session-with-nothing-pending", TenantId, CancellationToken.None);

        Assert.Empty(transport.SentEvents);
    }

    [Fact]
    public async Task RebroadcastPendingCardsAsync_ApprovedEntry_NotRebroadcast()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueDecision(gate, ApprovalDecision.Approved, Ctx());
        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileReviewAsync(proposal, context);
        transport.SentEvents.Clear();

        await gate.RebroadcastPendingCardsAsync(context.SessionId, TenantId, CancellationToken.None);

        Assert.Empty(transport.SentEvents);
    }

    [Fact]
    public async Task RebroadcastPendingCardsAsync_OtherSessionsPendingEntries_NotIncluded()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);
        transport.SentEvents.Clear();

        await gate.RebroadcastPendingCardsAsync("a-completely-different-session", TenantId, CancellationToken.None);

        Assert.Empty(transport.SentEvents);
    }

    [Fact]
    public async Task RebroadcastPendingCardsAsync_ResubmittedPendingEntry_CarriesPriorAmendments()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var priorAmendments = new Dictionary<string, object?> { ["title"] = "Edited before expiry" };
        var expiredEntry = new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: "session-test",
            TenantId: TenantId,
            UserId: "user-123",
            ReviewerUserId: "reviewer-456",
            OperationType: "CreateOrder",
            Envelope: CreateTestInput().context.Affidavit,
            Status: ReviewStatus.Pending,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-20),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            Amendments: priorAmendments);
        await FileExpiredAsync(store, expiredEntry);

        var filing = await gate.ResubmitAsync(expiredEntry.EntryId, Ctx());
        var newEntryId = Assert.IsType<ReviewFilingResult.RequiresReview>(filing).EntryId;
        transport.SentEvents.Clear();

        await gate.RebroadcastPendingCardsAsync("session-test", TenantId, CancellationToken.None);

        var broadcast = Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
        var request = Assert.IsType<EvidenceCardRequest>(broadcast.Payload);
        Assert.Equal(newEntryId, request.DocketId);
        Assert.NotNull(request.PriorAmendments);
        Assert.Equal("Edited before expiry", request.PriorAmendments!["title"]);
    }

    // ── The injectable clock (protocol GT-4, DK-1) ───────────────────────────

    /// <summary>The instant every clock test below starts from — arbitrary, but fixed and readable.</summary>
    private static readonly DateTimeOffset ClockOrigin = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FileForReviewAsync_FakeClock_StampsCreatedAtNow_AndExpiresAtNowPlusTtl()
    {
        var clock = new FakeTimeProvider(ClockOrigin);
        var ttl = TimeSpan.FromMinutes(17);
        var (gate, _, store) = CreateGate(
            ReviewRequirement.ReviewerConfirmation,
            new AffiantCoreOptions { DefaultDocketTtl = ttl },
            clock);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);

        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.NotNull(entry);
        Assert.Equal(ClockOrigin, entry.CreatedAt);
        Assert.Equal(ClockOrigin + ttl, entry.ExpiresAt);
    }

    [Fact]
    public async Task HandleDecisionAsync_DecisionExactlyAtExpiresAt_IsRefusedAsExpired()
    {
        var clock = new FakeTimeProvider(ClockOrigin);
        var ttl = TimeSpan.FromMinutes(30);
        var (gate, transport, store) = CreateGate(
            ReviewRequirement.ReviewerConfirmation,
            new AffiantCoreOptions { DefaultDocketTtl = ttl },
            clock);
        transport.HasLiveWaiter = false;

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);

        // Land the decision on the deadline itself — DK-1's boundary is inclusive, so this is late.
        clock.Advance(ttl);

        var (outcome, _) = await gate.HandleDecisionAsync(
            context.EntryId!.Value, ApprovalDecision.Approved, Ctx());

        Assert.Equal(
            DocketRefusalCodes.DecisionExpired,
            Assert.IsType<ReviewOutcome.Refused>(outcome).Code);
        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.Equal(ReviewStatus.Expired, entry!.Status);
        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.DocketExpired);
    }

    [Fact]
    public async Task HandleDecisionAsync_DecisionOneTickBeforeExpiresAt_IsStillAccepted()
    {
        var clock = new FakeTimeProvider(ClockOrigin);
        var ttl = TimeSpan.FromMinutes(30);
        var (gate, transport, store) = CreateGate(
            ReviewRequirement.ReviewerConfirmation,
            new AffiantCoreOptions { DefaultDocketTtl = ttl },
            clock);
        transport.HasLiveWaiter = false;

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);

        clock.Advance(ttl - TimeSpan.FromMilliseconds(1));

        var (outcome, _) = await gate.HandleDecisionAsync(
            context.EntryId!.Value, ApprovalDecision.Approved, Ctx());

        Assert.IsType<ReviewOutcome.Approved>(outcome);
        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.Equal(ReviewStatus.Approved, entry!.Status);
    }

    [Fact]
    public async Task GetDocketEntryAsync_PastExpiresAt_ReadsExpiredBeforeAnySweepOrDecision()
    {
        var clock = new FakeTimeProvider(ClockOrigin);
        var ttl = TimeSpan.FromMinutes(30);
        var (gate, _, store) = CreateGate(
            ReviewRequirement.ReviewerConfirmation,
            new AffiantCoreOptions { DefaultDocketTtl = ttl },
            clock);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);
        var entryId = context.EntryId!.Value;

        Assert.Equal(ReviewStatus.Pending, (await store.GetDocketEntryAsync(entryId, default))!.Status);

        clock.Advance(ttl);

        // Nothing has swept and nobody has decided — the read alone reports the state.
        Assert.Equal(ReviewStatus.Expired, (await store.GetDocketEntryAsync(entryId, default))!.Status);

        // ...and the row itself is still Pending underneath, which is what leaves the guarded
        // transition for the sweep (or a decision) to win exactly once.
        Assert.IsType<DocketTransitionResult.Transitioned>(await store.TransitionAsync(
            entryId, new DocketScope(TenantId), ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Expired), default));
    }

    [Fact]
    public async Task ListPendingBySessionAsync_PastExpiresAt_NoLongerListsTheEntry()
    {
        var clock = new FakeTimeProvider(ClockOrigin);
        var ttl = TimeSpan.FromMinutes(30);
        var (gate, transport, store) = CreateGate(
            ReviewRequirement.ReviewerConfirmation,
            new AffiantCoreOptions { DefaultDocketTtl = ttl },
            clock);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);
        Assert.Single(await store.ListPendingBySessionAsync(context.SessionId, default));

        clock.Advance(ttl);

        Assert.Empty(await store.ListPendingBySessionAsync(context.SessionId, default));

        // Which is also what stops a reconnect from re-broadcasting a card that has run out.
        transport.SentEvents.Clear();
        await gate.RebroadcastPendingCardsAsync(context.SessionId, TenantId, CancellationToken.None);
        Assert.DoesNotContain(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
    }

    [Fact]
    public async Task ResubmitAsync_LapsedButUnswept_Succeeds_WithoutSweepOrPriorDecision()
    {
        var clock = new FakeTimeProvider(ClockOrigin);
        var ttl = TimeSpan.FromMinutes(30);
        var (gate, _, store) = CreateGate(
            ReviewRequirement.ReviewerConfirmation,
            new AffiantCoreOptions { DefaultDocketTtl = ttl },
            clock);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);
        var entryId = context.EntryId!.Value;

        clock.Advance(ttl);

        // No sweep, and no late decision either — the read-time expiry state is the only thing
        // saying this entry is resubmittable.
        var filing = await gate.ResubmitAsync(entryId, Ctx());

        var requiresReview = Assert.IsType<ReviewFilingResult.RequiresReview>(filing);
        Assert.NotEqual(entryId, requiresReview.EntryId);

        var superseded = await store.GetDocketEntryAsync(entryId, default);
        Assert.Equal(ReviewStatus.Expired, superseded!.Status);
        Assert.Equal(requiresReview.EntryId, superseded.ResubmittedTo);

        var fresh = await store.GetDocketEntryAsync(requiresReview.EntryId, default);
        Assert.Equal(ReviewStatus.Pending, fresh!.Status);
        Assert.Equal(clock.GetUtcNow() + ttl, fresh.ExpiresAt);
    }

    [Fact]
    public async Task FileForReviewAsync_ReplayOfALapsedEntry_ReportsExpired_WithoutRebroadcasting()
    {
        var clock = new FakeTimeProvider(ClockOrigin);
        var ttl = TimeSpan.FromMinutes(30);
        var (gate, transport, _) = CreateGate(
            ReviewRequirement.ReviewerConfirmation,
            new AffiantCoreOptions { DefaultDocketTtl = ttl },
            clock);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);
        transport.SentEvents.Clear();

        clock.Advance(ttl);

        var replay = await gate.FileForReviewAsync(proposal, context);

        var decided = Assert.IsType<ReviewFilingResult.Decided>(replay);
        Assert.IsType<ReviewOutcome.Expired>(decided.Outcome);
        Assert.DoesNotContain(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
    }
}
#pragma warning restore AFFIANT0002
