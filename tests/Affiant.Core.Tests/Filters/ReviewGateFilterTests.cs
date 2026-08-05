namespace Affiant.Core.Tests.Filters;

using System.Diagnostics;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Backend-free tests for the neutral ReviewGateFilter (completion stage). Covers:
///   1. Graceful degradation — no-op when IReviewContextProvider or ReviewGate not registered.
///   2. Non-WriteProposal results — filter is a no-op.
///   3. WriteProposal → ReviewGate path — proposal is routed through ReviewGate.FileReviewAsync.
///
/// The filter resolves IReviewContextProvider / ReviewGate from ToolInvocationContext.Services
/// (the pipeline's per-invocation scope). These tests supply that scope directly.
/// </summary>
public class ReviewGateFilterTests
{
    private static readonly ReviewGateFilter Filter = new(NullLogger<ReviewGateFilter>.Instance);

    private static string BuildWriteProposalJson(string toolName) =>
        $$"""{"$type":"write","toolName":"{{toolName}}","timestamp":"2026-01-01T00:00:00Z","envelope":null}""";

    private static ToolInvocationContext Ctx(IServiceProvider services, object? toolResult)
    {
        var ctx = new ToolInvocationContext
        {
            FunctionName = "DoWrite",
            PluginName = "WritePlugin",
            Arguments = new Dictionary<string, object?>(),
            Services = services,
        };
        return ctx;
    }

    private static Task RunWithResult(IServiceProvider services, object? toolResult)
    {
        var ctx = Ctx(services, toolResult);
        return Filter.OnToolInvocationAsync(ctx, c => { c.Result = toolResult; return Task.CompletedTask; });
    }

    // ── Graceful degradation ─────────────────────────────────────────────────

    [Fact]
    public async Task NoReviewContextProvider_IsNoOp()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        var ex = await Record.ExceptionAsync(() =>
            RunWithResult(services, BuildWriteProposalJson("DoWrite")));

        Assert.Null(ex);
    }

    [Fact]
    public async Task NonWriteProposalResult_IsNoOp()
    {
        var docketStore = new FakeDocketStore();
        var services = BuildReviewGateStack(docketStore).BuildServiceProvider();
        using var scope = services.CreateScope();

        var ex = await Record.ExceptionAsync(() =>
            RunWithResult(scope.ServiceProvider, "plain text result"));

        Assert.Null(ex);
        Assert.Empty(docketStore.Filed);
    }

    // ── WriteProposal → ReviewGate path ─────────────────────────────────────

    [Fact]
    public async Task WriteProposalResult_RoutesToReviewGate()
    {
        var docketStore = new FakeDocketStore();
        var services = BuildReviewGateStack(docketStore).BuildServiceProvider();
        using var scope = services.CreateScope();

        await RunWithResult(scope.ServiceProvider, BuildWriteProposalJson("DoWrite"));

        // StandingOrderPolicy auto-approves → DocketEntry must have been filed and approved.
        Assert.Single(docketStore.Filed);
        Assert.Equal(ReviewStatus.Approved, docketStore.Filed[0].Status);
        Assert.Equal("DoWrite", docketStore.Filed[0].OperationType);
    }

    // ── P5a: non-blocking filing, RequiresReview vs Decided ─────────────────

    [Fact]
    public async Task Decided_StandingOrderAutoApprove_DoesNotTerminate_ResultUntouched()
    {
        // Control for the pair below: StandingOrderPolicy → Decided, not RequiresReview.
        var docketStore = new FakeDocketStore();
        var services = BuildReviewGateStack(docketStore).BuildServiceProvider();
        using var scope = services.CreateScope();

        var proposalJson = BuildWriteProposalJson("DoWrite");
        var ctx = Ctx(scope.ServiceProvider, proposalJson);
        await Filter.OnToolInvocationAsync(ctx, c => { c.Result = proposalJson; return Task.CompletedTask; });

        Assert.False(ctx.Terminate);
        Assert.Equal(proposalJson, ctx.Result); // untouched — model keeps the tool's own result
    }

    [Fact]
    public async Task RequiresReview_NoStandingOrder_TerminatesTurn_WithTurnEndingMessage()
    {
        // No IApprovalPolicy registered → evaluator's built-in fallback is ReviewerConfirmation, so
        // FileForReviewAsync returns RequiresReview (a human must act) instead of auto-deciding.
        var docketStore = new FakeDocketStore();
        var transport = new RecordingStreamingTransport();
        var services = BuildReviewGateStack(docketStore, transport, registerStandingOrderPolicy: false)
            .BuildServiceProvider();
        using var scope = services.CreateScope();

        var proposalJson = BuildWriteProposalJson("DoWrite");
        var ctx = Ctx(scope.ServiceProvider, proposalJson);
        await Filter.OnToolInvocationAsync(ctx, c => { c.Result = proposalJson; return Task.CompletedTask; });

        Assert.True(ctx.Terminate);
        var resultText = Assert.IsType<string>(ctx.Result);
        Assert.NotEqual(proposalJson, resultText);
        Assert.Contains("filed for review", resultText, StringComparison.OrdinalIgnoreCase);

        // FileForReviewAsync itself already broadcasts the Evidence Card — proves the non-blocking
        // path actually ran (not a no-op) without this filter doing any broadcasting of its own.
        var evidenceCard = Assert.Single(
            transport.Broadcasts, b => b.EventType == TransportEvent.EvidenceCardRequest);
        Assert.Equal("session-test", evidenceCard.GroupId);

        Assert.Single(docketStore.Filed);
        Assert.Equal(ReviewStatus.Pending, docketStore.Filed[0].Status);
    }

    [Fact]
    public async Task RequiresReview_NeverCallsAwaitEventAsync_ProvingNonBlockingPath()
    {
        // AwaitBlockingTransport throws the instant AwaitEvidenceCardResponseAsync is called — the blocking
        // FileReviewAsync path would call it and this test would fail; FileForReviewAsync never does.
        var docketStore = new FakeDocketStore();
        var transport = new AwaitBlockingTransport();
        var services = BuildReviewGateStack(docketStore, transport, registerStandingOrderPolicy: false)
            .BuildServiceProvider();
        using var scope = services.CreateScope();

        var proposalJson = BuildWriteProposalJson("DoWrite");
        var ctx = Ctx(scope.ServiceProvider, proposalJson);

        var ex = await Record.ExceptionAsync(() =>
            Filter.OnToolInvocationAsync(ctx, c => { c.Result = proposalJson; return Task.CompletedTask; }));

        Assert.Null(ex);
        Assert.True(ctx.Terminate);
    }

    // ── P1a: filing failure (affiant#22 / FV-9) ──────────────────────────────

    private static ActivityListener FrameworkListener() => new()
    {
        // Hardcoded "Affiant.Framework" (matching ToolTracingFilterTests' convention) rather than
        // AffiantTelemetry.AffiantActivitySource.Name: referencing the static property here would
        // re-enter AffiantTelemetry's own type initializer if this is the first touch of the class in
        // the test process (ActivitySource's constructor notifies already-registered listeners
        // synchronously), throwing a NullReferenceException on the not-yet-assigned static field.
        ShouldListenTo = source => source.Name == "Affiant.Framework",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    [Fact]
    public async Task FilingThrows_ModelReceivesTypedToolError_NotFiledMessage()
    {
        var docketStore = new ThrowingDocketStore();
        var transport = new RecordingStreamingTransport();
        var services = BuildReviewGateStack(docketStore, transport).BuildServiceProvider();
        using var scope = services.CreateScope();

        var ctx = Ctx(scope.ServiceProvider, BuildWriteProposalJson("DoWrite"));
        await Filter.OnToolInvocationAsync(ctx, c => { c.Result = BuildWriteProposalJson("DoWrite"); return Task.CompletedTask; });

        var resultJson = Assert.IsType<string>(ctx.Result);
        using var doc = JsonDocument.Parse(resultJson);
        Assert.Equal("error", doc.RootElement.GetProperty("$type").GetString());
        Assert.Equal("REVIEW_FILING_FAILED", doc.RootElement.GetProperty("code").GetString());
        Assert.False(doc.RootElement.GetProperty("retryable").GetBoolean());

        var message = doc.RootElement.GetProperty("message").GetString();
        Assert.NotNull(message);
        Assert.Contains("NOT filed", message);
        Assert.Contains("NOT", message.Replace("NOT filed", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("queued for review", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FilingThrows_EmitsOTelEvent_ObservedViaRealActivityListener()
    {
        using var listener = FrameworkListener();
        ActivitySource.AddActivityListener(listener);
        using var span = AffiantTelemetry.AffiantActivitySource.StartActivity("invoke_agent");
        Assert.NotNull(span);

        var docketStore = new ThrowingDocketStore();
        var transport = new RecordingStreamingTransport();
        var services = BuildReviewGateStack(docketStore, transport).BuildServiceProvider();
        using var scope = services.CreateScope();

        var ctx = Ctx(scope.ServiceProvider, null);
        await Filter.OnToolInvocationAsync(ctx, c => { c.Result = BuildWriteProposalJson("DoWrite"); return Task.CompletedTask; });

        var evt = Assert.Single(span!.Events, e => e.Name == "affiant.review.filing_failed");
        var tags = evt.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("REVIEW_FILING_FAILED", tags["tool_error.code"]);
        Assert.Equal("InvalidOperationException", tags["exception.type"]);
    }

    [Fact]
    public async Task FilingThrows_SystemNotificationBroadcastAttempted()
    {
        var docketStore = new ThrowingDocketStore();
        var transport = new RecordingStreamingTransport();
        var services = BuildReviewGateStack(docketStore, transport).BuildServiceProvider();
        using var scope = services.CreateScope();

        var ctx = Ctx(scope.ServiceProvider, null);
        await Filter.OnToolInvocationAsync(ctx, c => { c.Result = BuildWriteProposalJson("DoWrite"); return Task.CompletedTask; });

        var notification = Assert.Single(
            transport.Broadcasts, b => b.EventType == TransportEvent.SystemNotification);
        Assert.Equal("session-test", notification.GroupId);

        // P1b: the call site migrated from an anonymous { level, message } object to the named
        // SystemNotificationPayload record — same wire shape, now a real type.
        var payload = Assert.IsType<SystemNotificationPayload>(notification.Payload);
        Assert.Equal("error", payload.Level);
        Assert.Contains("could not be filed for review", payload.Message);
    }

    [Fact]
    public async Task FilingThrows_NotifyBroadcastItselfFails_DoesNotMaskToolError()
    {
        var docketStore = new ThrowingDocketStore();
        var transport = new RecordingStreamingTransport { ThrowOnSystemNotification = true };
        var services = BuildReviewGateStack(docketStore, transport).BuildServiceProvider();
        using var scope = services.CreateScope();

        var ctx = Ctx(scope.ServiceProvider, null);
        var ex = await Record.ExceptionAsync(() =>
            Filter.OnToolInvocationAsync(ctx, c => { c.Result = BuildWriteProposalJson("DoWrite"); return Task.CompletedTask; }));

        Assert.Null(ex); // best-effort notify failure must not escape
        var resultJson = Assert.IsType<string>(ctx.Result);
        Assert.Contains("REVIEW_FILING_FAILED", resultJson);
    }

    [Fact]
    public async Task OperationCanceledException_StillPropagates_NoToolErrorRewrite()
    {
        var docketStore = new CancellingDocketStore();
        var transport = new RecordingStreamingTransport();
        var services = BuildReviewGateStack(docketStore, transport).BuildServiceProvider();
        using var scope = services.CreateScope();

        var ctx = Ctx(scope.ServiceProvider, null);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Filter.OnToolInvocationAsync(ctx, c => { c.Result = BuildWriteProposalJson("DoWrite"); return Task.CompletedTask; }));

        // Cancellation must not be rewritten to a ToolError, and no best-effort notify attempted.
        Assert.DoesNotContain("REVIEW_FILING_FAILED", ctx.Result as string ?? string.Empty);
        Assert.Empty(transport.Broadcasts);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ReviewContext BuildReviewContext() => new(
        SessionId: "session-test",
        TenantId: "tenant-test",
        UserId: "user-test",
        ReviewerUserId: "reviewer-test",
        Affidavit: new Affidavit(
            OperationType: "create",
            EntityType: "TestEntity",
            EntityId: null,
            Fields: [],
            AggregateConfidence: 1.0f,
            Warnings: [],
            RequiresConfirmation: false));

    private ServiceCollection BuildReviewGateStack(
        IDocketStore docketStore,
        IStreamingTransport? transport = null,
        bool registerStandingOrderPolicy = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);

        services.AddScoped<ReviewGate>();
        services.AddSingleton(transport ?? new UnusedStreamingTransport());
        services.AddSingleton(docketStore);
        if (registerStandingOrderPolicy)
            services.AddSingleton<IApprovalPolicy>(new StandingOrderPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();
        services.AddSingleton<IReviewContextProvider>(new ConstantReviewContextProvider(BuildReviewContext()));
        return services;
    }

    internal sealed class ConstantReviewContextProvider(ReviewContext context) : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => context;
    }

    private sealed class StandingOrderPolicy : IApprovalPolicy
    {
        public Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
            => Task.FromResult<ReviewRequirement?>(ReviewRequirement.StandingOrder);
    }

    private sealed class UnusedStreamingTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new InvalidOperationException("UnusedStreamingTransport.SendAsync should not be called");

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new InvalidOperationException("UnusedStreamingTransport.BroadcastToGroupAsync should not be called");

        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException("UnusedStreamingTransport.AwaitEvidenceCardResponseAsync should not be called");
    }

    /// <summary>
    /// Allows SendAsync/BroadcastToGroupAsync (needed for the Evidence Card broadcast) but throws
    /// the instant AwaitEvidenceCardResponseAsync is called — proves
    /// RequiresReview_NeverCallsAwaitEventAsync_ProvingNonBlockingPath proves the filter genuinely calls the non-blocking
    /// ReviewGate.FileForReviewAsync, not the blocking FileReviewAsync (which would call
    /// AwaitEvidenceCardResponseAsync and either throw here or hang forever on a transport that
    /// doesn't).
    /// </summary>
    private sealed class AwaitBlockingTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException(
                "AwaitEvidenceCardResponseAsync was called — the non-blocking FileForReviewAsync path must never do this.");
    }

    /// <summary>Records every broadcast; used to assert the best-effort SystemNotification path.</summary>
    private sealed class RecordingStreamingTransport : IStreamingTransport
    {
        public List<(string GroupId, TransportEvent EventType, object Payload)> Broadcasts { get; } = [];

        /// <summary>When true, the SystemNotification broadcast itself throws (P1a guard test).</summary>
        public bool ThrowOnSystemNotification { get; set; }

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new InvalidOperationException("RecordingStreamingTransport.SendAsync should not be called");

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            if (ThrowOnSystemNotification && eventType == TransportEvent.SystemNotification)
                throw new InvalidOperationException("simulated SystemNotification broadcast failure");

            Broadcasts.Add((groupId, eventType, payload));
            return Task.CompletedTask;
        }

        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException("RecordingStreamingTransport.AwaitEvidenceCardResponseAsync should not be called");
    }

    /// <summary>FileDocketEntryAsync always throws — simulates the docket store being down (FV-9).</summary>
    private sealed class ThrowingDocketStore : IDocketStore
    {
        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => Task.CompletedTask;

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<ConversationContext?>(null);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
            => throw new InvalidOperationException("simulated docket store outage");

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
            => Task.FromResult(0);

        public Task<int> TryConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>FileDocketEntryAsync throws OperationCanceledException — must propagate, never rewritten.</summary>
    private sealed class CancellingDocketStore : IDocketStore
    {
        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => Task.CompletedTask;

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<ConversationContext?>(null);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
            => throw new OperationCanceledException("simulated cancellation during filing");

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
            => Task.FromResult(0);

        public Task<int> TryConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeDocketStore : IDocketStore
    {
        public readonly List<DocketEntry> Filed = [];

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
        {
            Filed.Add(entry);
            return Task.CompletedTask;
        }

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(Filed.FirstOrDefault(e => e.EntryId == entryId));

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
        {
            var idx = Filed.FindIndex(e => e.EntryId == entryId && e.Status == ReviewStatus.Pending);
            if (idx < 0) return Task.FromResult(0);
            Filed[idx] = Filed[idx] with { Status = status };
            return Task.FromResult(1);
        }

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
        {
            var idx = Filed.FindIndex(e => e.EntryId == entryId);
            if (idx >= 0) Filed[idx] = Filed[idx] with { Amendments = amendments };
            return Task.CompletedTask;
        }

        public Task<int> TryConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
        {
            var idx = Filed.FindIndex(e =>
                e.EntryId == entryId && e.Status == ReviewStatus.Expired && e.ResubmittedTo is null);
            if (idx < 0) return Task.FromResult(0);
            Filed[idx] = Filed[idx] with { ResubmittedTo = newEntryId };
            return Task.FromResult(1);
        }

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(Filed.FirstOrDefault(e => e.ResubmittedTo == entryId));

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => Task.CompletedTask;

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<ConversationContext?>(null);

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
            => Task.CompletedTask;
    }
}
