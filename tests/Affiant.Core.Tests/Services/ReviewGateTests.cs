namespace Affiant.Core.Tests.Services;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Unit tests for the <see cref="ReviewGate"/> state machine.
/// Uses inline test doubles (FakeStreamingTransport, InMemoryDocketStore, FakeApprovalPolicy).
/// TODO (Story 6.12): Replace inline doubles with shared fixtures from Affiant.TestInfrastructure.
/// </summary>
public class ReviewGateTests
{
    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeStreamingTransport : IStreamingTransport
    {
        private readonly Queue<EvidenceCardResponse> _responses = new();
        private bool _simulateTimeout;
        private bool _hangUntilCancelled;
        private Func<Task>? _beforeTimeoutThrow;

        public List<(string GroupId, TransportEvent EventType, object Payload)> SentEvents { get; } = [];
        public List<EvidenceCardResponse> DeliveredResponses { get; } = [];

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

        public void EnqueueResponse(EvidenceCardResponse response) => _responses.Enqueue(response);

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
        /// Never returns a response — <see cref="AwaitEventAsync{T}"/> only unblocks when
        /// <paramref name="ct"/> is cancelled, so a real (short) TTL genuinely drives the
        /// <c>CancelAfter</c> window instead of the fake short-circuiting synchronously.
        /// </summary>
        public void HangUntilCancelled() => _hangUntilCancelled = true;

        public bool TryDeliverResponse(Guid docketId, EvidenceCardResponse response)
        {
            DeliveredResponses.Add(response);
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

        public async IAsyncEnumerable<TransportMessage> ReceiveAsync(
            string connectionId,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async Task<T> AwaitEventAsync<T>(string sessionGroupId, Guid docketId, CancellationToken ct = default)
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

            if (_responses.TryDequeue(out var response) && response is T typed)
                return typed;

            throw new InvalidOperationException($"FakeStreamingTransport: no queued response for {typeof(T).Name}");
        }
    }

    private sealed class InMemoryDocketStore : IDocketStore
    {
        private readonly Dictionary<Guid, DocketEntry> _entries = [];
        private readonly Dictionary<string, ConversationContext> _contexts = [];

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _contexts[sessionId] = context;
            return Task.CompletedTask;
        }

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_contexts.TryGetValue(sessionId, out var ctx) ? ctx : null);
        }

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_entries.ContainsKey(entry.EntryId))
                _entries[entry.EntryId] = entry;
            return Task.CompletedTask;
        }

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_entries.TryGetValue(entryId, out var e) ? e : null);
        }

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(entryId, out var existing) || existing.Status != ReviewStatus.Pending)
                return Task.FromResult(0);
            _entries[entryId] = existing with { Status = status };
            return Task.FromResult(1);
        }

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_entries.TryGetValue(entryId, out var existing))
                _entries[entryId] = existing with { Amendments = amendments };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<DocketEntry> results = _entries.Values
                .Where(e => e.SessionId == sessionId && e.Status == ReviewStatus.Pending)
                .ToList();
            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<DocketEntry> results = _entries.Values
                .Where(e => e.Status == ReviewStatus.Pending && e.ExpiresAt <= expiresBeforeUtc)
                .ToList();
            return Task.FromResult(results);
        }

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var ids = entryIds.ToHashSet();
            foreach (var id in ids)
            {
                if (_entries.TryGetValue(id, out var entry) && entry.Status == ReviewStatus.Pending)
                    _entries[id] = entry with { Status = ReviewStatus.Expired };
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApprovalPolicyEvaluator(ReviewRequirement requirement) : IApprovalPolicyEvaluator
    {
        public Task<ReviewRequirement> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
            => Task.FromResult(requirement);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (ReviewGate gate, FakeStreamingTransport transport, InMemoryDocketStore docketStore)
        CreateGate(
            ReviewRequirement reviewRequirement = ReviewRequirement.ReviewerConfirmation,
            AffiantCoreOptions? options = null)
    {
        var transport = new FakeStreamingTransport();
        var store = new InMemoryDocketStore();
        var evaluator = new FakeApprovalPolicyEvaluator(reviewRequirement);
        var gate = new ReviewGate(
            transport, store, evaluator, options ?? new AffiantCoreOptions(), NullLogger<ReviewGate>.Instance);
        return (gate, transport, store);
    }

    private static (WriteProposal proposal, ReviewContext context) CreateTestInput(Guid? entryId = null)
    {
        var affidavit = new Affidavit(
            OperationType: "CreateOrder",
            EntityType: "Order",
            EntityId: null,
            Fields: [new AffidavitField("title", "Test Order", null,
                ProvenanceChain.From(ProvenanceTag.FromInference("title", 0.8f)))],
            AggregateConfidence: 0.8f,
            Warnings: [],
            RequiresConfirmation: true);

        var proposal = new WriteProposal("CreateOrder", DateTimeOffset.UtcNow, affidavit);
        var context = new ReviewContext(
            SessionId: "session-test",
            TenantId: "tenant-default",
            UserId: "user-123",
            ReviewerUserId: "reviewer-456",
            Affidavit: affidavit,
            EntryId: entryId);
        return (proposal, context);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FileReviewAsync_ReviewerConfirmation_Approved_ReturnsApproved()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueResponse(new EvidenceCardResponse(Guid.Empty, ApprovalDecision.Approved));

        var (proposal, context) = CreateTestInput();
        var outcome = await gate.FileReviewAsync(proposal, context);

        Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
    }

    [Fact]
    public async Task FileReviewAsync_StandingOrder_AutoApproves_WithoutEvidenceCard()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.StandingOrder);

        var (proposal, context) = CreateTestInput();
        var outcome = await gate.FileReviewAsync(proposal, context);

        Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.Empty(transport.SentEvents);
    }

    [Fact]
    public async Task FileReviewAsync_ClientRejects_ReturnsRejected()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueResponse(new EvidenceCardResponse(Guid.Empty, ApprovalDecision.Rejected, "Budget exceeded"));

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
        transport.SimulateTimeout(beforeThrow: () =>
            store.UpdateReviewStatusAsync(entryId, ReviewStatus.Approved, default));

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
    public async Task FileReviewAsync_ReferralRequired_ReturnsReferral()
    {
        var (gate, _, store) = CreateGate(ReviewRequirement.ReferralRequired);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var outcome = await gate.FileReviewAsync(proposal, context);

        Assert.IsType<ReviewOutcome.Referral>(outcome);

        var entry = await store.GetDocketEntryAsync(context.EntryId!.Value, default);
        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Deferred, entry.Status);
    }

    [Fact]
    public async Task FileReviewAsync_DoubleSubmit_Idempotent_SingleEntry()
    {
        var entryId = Guid.NewGuid();
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueResponse(new EvidenceCardResponse(Guid.Empty, ApprovalDecision.Approved));

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
    public async Task FileReviewAsync_MultiParty_TreatedAsReviewerConfirmation()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.MultiParty);
        transport.EnqueueResponse(new EvidenceCardResponse(Guid.Empty, ApprovalDecision.Approved));

        var (proposal, context) = CreateTestInput();
        var outcome = await gate.FileReviewAsync(proposal, context);

        Assert.IsType<ReviewOutcome.Approved>(outcome);
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
        transport.EnqueueResponse(
            new EvidenceCardResponse(entryId, ApprovalDecision.Approved, Amendments: amendments));

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

    [Fact]
    public async Task FileReviewAsync_ApprovedWithoutAmendments_LeavesAmendmentsNull()
    {
        var entryId = Guid.NewGuid();
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueResponse(new EvidenceCardResponse(entryId, ApprovalDecision.Approved));

        var (proposal, context) = CreateTestInput(entryId);
        await gate.FileReviewAsync(proposal, context);

        var entry = await store.GetDocketEntryAsync(entryId, default);
        Assert.NotNull(entry);
        Assert.Null(entry.Amendments);
    }

    [Fact]
    public async Task HandleDecisionAsync_LiveWaiter_ThreadsAmendmentsIntoDeliveredResponse()
    {
        var (gate, transport, _) = CreateGate();
        transport.HasLiveWaiter = true;
        var entryId = Guid.NewGuid();
        var amendments = new Dictionary<string, object?> { ["title"] = "Reviewer-Edited Title" };

        var (outcome, createdAt) = await gate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, amendments);

        // Live path: the awaiting FileReviewAsync call owns the outcome — this method returns nulls.
        Assert.Null(outcome);
        Assert.Null(createdAt);

        var delivered = Assert.Single(transport.DeliveredResponses);
        Assert.Equal(entryId, delivered.DocketId);
        Assert.Equal(ApprovalDecision.Approved, delivered.Decision);
        Assert.NotNull(delivered.Amendments);
        Assert.Equal("Reviewer-Edited Title", delivered.Amendments!["title"]);
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
            entry.EntryId, ApprovalDecision.Approved, amendments);

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
            entry.EntryId, ApprovalDecision.Rejected, amendments);

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
            transport.EnqueueResponse(new EvidenceCardResponse(Guid.Empty, ApprovalDecision.Approved));
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
        transport.HasLiveWaiter = false; // FileForReviewAsync never calls AwaitEventAsync — no waiter exists

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var filing = await gate.FileForReviewAsync(proposal, context);

        var requiresReview = Assert.IsType<ReviewFilingResult.RequiresReview>(filing);
        Assert.Equal(context.EntryId!.Value, requiresReview.EntryId);
        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);

        // Immediately deliver a decision — as if the host received it right after filing,
        // long before any FileReviewAsync-style blocking await could exist.
        var amendments = new Dictionary<string, object?> { ["title"] = "A1 regression edit" };
        var (outcome, createdAt) = await gate.HandleDecisionAsync(
            requiresReview.EntryId, ApprovalDecision.Approved, amendments);

        Assert.IsType<ReviewOutcome.Approved>(outcome);
        Assert.NotNull(createdAt);

        var entry = await store.GetDocketEntryAsync(requiresReview.EntryId, default);
        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Approved, entry.Status);
        Assert.NotNull(entry.Amendments);
        Assert.Equal("A1 regression edit", entry.Amendments!["title"]);
    }

    [Fact]
    public async Task FileForReviewAsync_StandingOrder_ReturnsDecided_WithoutEvidenceCard()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.StandingOrder);

        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        var filing = await gate.FileForReviewAsync(proposal, context);

        var decided = Assert.IsType<ReviewFilingResult.Decided>(filing);
        Assert.IsType<ReviewOutcome.Approved>(decided.Outcome);
        Assert.Empty(transport.SentEvents);

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
            Status: ReviewStatus.Expired,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-15),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            Amendments: null);
        await store.FileDocketEntryAsync(expiredEntry, default);

        var lateAmendments = new Dictionary<string, object?> { ["title"] = "Late reviewer edit" };
        var (outcome, createdAt) = await gate.HandleDecisionAsync(
            expiredEntry.EntryId, ApprovalDecision.Approved, lateAmendments);

        var expired = Assert.IsType<ReviewOutcome.Expired>(outcome);
        Assert.True(expired.AmendmentsPreserved);
        Assert.Null(createdAt); // the not-pending branch does not thread creation time

        var updated = await store.GetDocketEntryAsync(expiredEntry.EntryId, default);
        Assert.NotNull(updated);
        Assert.Equal(ReviewStatus.Expired, updated.Status); // status itself is not resurrected here
        Assert.NotNull(updated.Amendments);
        Assert.Equal("Late reviewer edit", updated.Amendments!["title"]);
    }

    [Fact]
    public async Task HandleDecisionAsync_NotFoundEntry_ReturnsExpired_AmendmentsPreservedFalse()
    {
        var (gate, transport, _) = CreateGate();
        transport.HasLiveWaiter = false;

        var (outcome, createdAt) = await gate.HandleDecisionAsync(
            Guid.NewGuid(), ApprovalDecision.Approved, new Dictionary<string, object?> { ["x"] = 1 });

        var expired = Assert.IsType<ReviewOutcome.Expired>(outcome);
        Assert.False(expired.AmendmentsPreserved); // nothing to persist onto a nonexistent entry
        Assert.Null(createdAt);
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

        Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.SystemNotification);
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
            TenantId: "tenant-default",
            UserId: "user-123",
            ReviewerUserId: "reviewer-456",
            OperationType: "CreateOrder",
            Envelope: CreateTestInput().context.Affidavit,
            Status: ReviewStatus.Expired,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-20),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            Amendments: priorAmendments);
        await store.FileDocketEntryAsync(expiredEntry, default);

        var filing = await gate.ResubmitAsync(expiredEntry.EntryId);

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

    [Fact]
    public async Task ResubmitAsync_NonExpiredEntry_ThrowsInvalidOperationException()
    {
        var (gate, _, store) = CreateGate();
        var pendingEntry = new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: "session-test",
            TenantId: "tenant-default",
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
            () => gate.ResubmitAsync(pendingEntry.EntryId));
    }

    [Fact]
    public async Task ResubmitAsync_UnknownEntry_ThrowsInvalidOperationException()
    {
        var (gate, _, _) = CreateGate();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.ResubmitAsync(Guid.NewGuid()));
    }
}
