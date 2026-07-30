namespace Affiant.Core.Tests.Services;

using System.Runtime.CompilerServices;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
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

        public List<(string GroupId, TransportEvent EventType, object Payload)> SentEvents { get; } = [];
        public List<EvidenceCardResponse> DeliveredResponses { get; } = [];

        /// <summary>When true, <see cref="TryDeliverResponse"/> simulates a live waiter.</summary>
        public bool HasLiveWaiter { get; set; }

        public void EnqueueResponse(EvidenceCardResponse response) => _responses.Enqueue(response);
        public void SimulateTimeout() => _simulateTimeout = true;

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

        public Task<T> AwaitEventAsync<T>(string sessionGroupId, Guid docketId, CancellationToken ct = default)
        {
            if (_simulateTimeout)
            {
                // Throw with a fresh cancelled token — not the caller's token — to simulate
                // the internal CTS timeout (distinct from caller cancellation).
                using var timeoutCts = new CancellationTokenSource();
                timeoutCts.Cancel();
                throw new OperationCanceledException("Simulated timeout", timeoutCts.Token);
            }

            if (_responses.TryDequeue(out var response) && response is T typed)
                return Task.FromResult(typed);

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
        CreateGate(ReviewRequirement reviewRequirement = ReviewRequirement.ReviewerConfirmation)
    {
        var transport = new FakeStreamingTransport();
        var store = new InMemoryDocketStore();
        var evaluator = new FakeApprovalPolicyEvaluator(reviewRequirement);
        var gate = new ReviewGate(transport, store, evaluator, NullLogger<ReviewGate>.Instance);
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
}
