namespace Affiant.Core.Tests.Services;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging;
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
        /// Never returns a response — <see cref="AwaitEvidenceCardResponseAsync"/> only unblocks
        /// when <paramref name="ct"/> is cancelled, so a real (short) TTL genuinely drives the
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

        public async Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(
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

            if (_responses.TryDequeue(out var response))
                return response;

            throw new InvalidOperationException("FakeStreamingTransport: no queued EvidenceCardResponse");
        }
    }

    /// <summary>
    /// All mutating/reading access is serialized under <see cref="_lock"/> — a plain
    /// <see cref="Dictionary{TKey,TValue}"/> is not safe for concurrent access even to distinct
    /// keys, and the D2 double-resubmit regression test below genuinely races two
    /// <see cref="ReviewGate.ResubmitAsync"/> calls against one instance of this store.
    /// </summary>
    private sealed class InMemoryDocketStore : IDocketStore
    {
        private readonly Dictionary<Guid, DocketEntry> _entries = [];
        private readonly Dictionary<string, ConversationContext> _contexts = [];
        private readonly object _lock = new();

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock) { _contexts[sessionId] = context; }
            return Task.CompletedTask;
        }

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock) { return Task.FromResult(_contexts.TryGetValue(sessionId, out var ctx) ? ctx : null); }
        }

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (!_entries.ContainsKey(entry.EntryId))
                    _entries[entry.EntryId] = entry;
            }
            return Task.CompletedTask;
        }

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock) { return Task.FromResult(_entries.TryGetValue(entryId, out var e) ? e : null); }
        }

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (!_entries.TryGetValue(entryId, out var existing) || existing.Status != ReviewStatus.Pending)
                    return Task.FromResult(0);
                _entries[entryId] = existing with { Status = status };
                return Task.FromResult(1);
            }
        }

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (!_entries.TryGetValue(entryId, out var existing)
                    || existing.Status != ReviewStatus.Expired
                    || existing.ResubmittedTo is not null)
                {
                    return Task.FromResult(0);
                }
                _entries[entryId] = existing with { ResubmittedTo = newEntryId };
                return Task.FromResult(1);
            }
        }

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock) { return Task.FromResult(_entries.Values.FirstOrDefault(e => e.ResubmittedTo == entryId)); }
        }

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_entries.TryGetValue(entryId, out var existing))
                    _entries[entryId] = existing with { Amendments = amendments };
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                IReadOnlyList<DocketEntry> results = _entries.Values
                    .Where(e => e.SessionId == sessionId && e.Status == ReviewStatus.Pending)
                    .OrderBy(e => e.CreatedAt)
                    .ToList();
                return Task.FromResult(results);
            }
        }

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                IReadOnlyList<DocketEntry> results = _entries.Values
                    .Where(e => e.Status == ReviewStatus.Pending)
                    .ToList();
                return Task.FromResult(results);
            }
        }

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                IReadOnlyList<DocketEntry> results = _entries.Values
                    .Where(e => e.Status == ReviewStatus.Pending && e.ExpiresAt <= expiresBeforeUtc)
                    .ToList();
                return Task.FromResult(results);
            }
        }

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var ids = entryIds.ToHashSet();
            lock (_lock)
            {
                foreach (var id in ids)
                {
                    if (_entries.TryGetValue(id, out var entry) && entry.Status == ReviewStatus.Pending)
                        _entries[id] = entry with { Status = ReviewStatus.Expired };
                }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApprovalPolicyEvaluator(ReviewRequirement requirement) : IApprovalPolicyEvaluator
    {
        public Task<ReviewRequirement> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
            => Task.FromResult(requirement);
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

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
            => inner.UpdateReviewStatusAsync(entryId, status, ct);

        public async Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
        {
            var result = await inner.ConsumeForResubmitAsync(entryId, newEntryId, ct);
            if (result > 0)
                cts.Cancel();
            return result;
        }

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => inner.GetResubmissionParentAsync(entryId, ct);

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
            => inner.UpdateAmendmentsAsync(entryId, amendments, ct);

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => inner.ListPendingBySessionAsync(sessionId, ct);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => inner.ListAllPendingAsync(ct);

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
            => inner.ListExpiredAsync(expiresBeforeUtc, ct);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
            => inner.MarkExpiredAsync(entryIds, ct);
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
                ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "title", 0.8f)))],
            AggregateConfidence: 0.8f,
            PopulatedConfidence: 0.8f,
            EmptyFieldCount: 0,
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

    // ── The amended Affidavit that travels beside the proposal ────────────────

    [Fact]
    public async Task FileReviewAsync_ApprovedWithAmendments_ReturnsTheAmendedAffidavitBesideTheProposal()
    {
        var entryId = Guid.NewGuid();
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueResponse(new EvidenceCardResponse(
            entryId,
            ApprovalDecision.Approved,
            Amendments: new Dictionary<string, object?> { ["title"] = "Reviewer-Edited Title" }));

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
        transport.EnqueueResponse(new EvidenceCardResponse(Guid.Empty, ApprovalDecision.Approved));

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
        transport.EnqueueResponse(new EvidenceCardResponse(
            entryId,
            ApprovalDecision.Approved,
            Amendments: new Dictionary<string, object?> { ["notes"] = "not a proposed field" }));

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
            lapsedEntry.EntryId, ApprovalDecision.Approved);

        var expired = Assert.IsType<ReviewOutcome.Expired>(outcome);
        Assert.False(expired.AmendmentsPreserved);
        Assert.Null(createdAt);

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

        await gate.HandleDecisionAsync(lapsedEntry.EntryId, ApprovalDecision.Rejected);

        // No DocketExpiryService sweep runs in this test — ResubmitAsync must not need one.
        var filing = await gate.ResubmitAsync(lapsedEntry.EntryId);

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

        var (firstOutcome, _) = await gate.HandleDecisionAsync(lapsedEntry.EntryId, ApprovalDecision.Approved);
        var (secondOutcome, _) = await gate.HandleDecisionAsync(lapsedEntry.EntryId, ApprovalDecision.Approved);

        Assert.IsType<ReviewOutcome.Expired>(firstOutcome);
        Assert.IsType<ReviewOutcome.Expired>(secondOutcome);

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
            lapsedEntry.EntryId, ApprovalDecision.Approved, amendments);

        var expired = Assert.IsType<ReviewOutcome.Expired>(outcome);
        Assert.True(expired.AmendmentsPreserved);

        var updated = await store.GetDocketEntryAsync(lapsedEntry.EntryId, default);
        Assert.NotNull(updated);
        Assert.Equal(ReviewStatus.Expired, updated.Status);
        Assert.NotNull(updated.Amendments);
        Assert.Equal("Late reviewer edit", updated.Amendments!["title"]);
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
        var gate = new ReviewGate(transport, store, evaluator, new AffiantCoreOptions(), capturingLogger);

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
            Amendments: null);
        await innerStore.FileDocketEntryAsync(expiredEntry, default);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gate.ResubmitAsync(expiredEntry.EntryId, cts.Token));

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

    // ── D2: double-resubmit race guard (affiant#31) ───────────────────────────

    [Fact]
    public async Task ResubmitAsync_GenuinelyConcurrentCallsOnSameExpiredEntry_ExactlyOneSucceeds_ExactlyOneNewPendingEntry()
    {
        var (gate, transport, store) = CreateGate(ReviewRequirement.ReviewerConfirmation);
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
            Amendments: null);
        await store.FileDocketEntryAsync(expiredEntry, default);

        async Task<ReviewFilingResult?> TryResubmitAsync()
        {
            try { return await gate.ResubmitAsync(expiredEntry.EntryId); }
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

        await gate.RebroadcastPendingCardsAsync(context.SessionId, CancellationToken.None);

        var broadcast = Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
        Assert.Equal(context.SessionId, broadcast.GroupId);
        var request = Assert.IsType<EvidenceCardRequest>(broadcast.Payload);
        Assert.Equal(entryId, request.DocketId);
    }

    [Fact]
    public async Task RebroadcastPendingCardsAsync_NoPendingEntriesInSession_NoBroadcast()
    {
        var (gate, transport, _) = CreateGate();

        await gate.RebroadcastPendingCardsAsync("session-with-nothing-pending", CancellationToken.None);

        Assert.Empty(transport.SentEvents);
    }

    [Fact]
    public async Task RebroadcastPendingCardsAsync_ApprovedEntry_NotRebroadcast()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        transport.EnqueueResponse(new EvidenceCardResponse(Guid.Empty, ApprovalDecision.Approved));
        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileReviewAsync(proposal, context);
        transport.SentEvents.Clear();

        await gate.RebroadcastPendingCardsAsync(context.SessionId, CancellationToken.None);

        Assert.Empty(transport.SentEvents);
    }

    [Fact]
    public async Task RebroadcastPendingCardsAsync_OtherSessionsPendingEntries_NotIncluded()
    {
        var (gate, transport, _) = CreateGate(ReviewRequirement.ReviewerConfirmation);
        var (proposal, context) = CreateTestInput(Guid.NewGuid());
        await gate.FileForReviewAsync(proposal, context);
        transport.SentEvents.Clear();

        await gate.RebroadcastPendingCardsAsync("a-completely-different-session", CancellationToken.None);

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
        var newEntryId = Assert.IsType<ReviewFilingResult.RequiresReview>(filing).EntryId;
        transport.SentEvents.Clear();

        await gate.RebroadcastPendingCardsAsync("session-test", CancellationToken.None);

        var broadcast = Assert.Single(transport.SentEvents, e => e.EventType == TransportEvent.EvidenceCardRequest);
        var request = Assert.IsType<EvidenceCardRequest>(broadcast.Payload);
        Assert.Equal(newEntryId, request.DocketId);
        Assert.NotNull(request.PriorAmendments);
        Assert.Equal("Edited before expiry", request.PriorAmendments!["title"]);
    }
}
