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

    private ServiceCollection BuildReviewGateStack(IDocketStore docketStore, IStreamingTransport? transport = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);

        services.AddScoped<ReviewGate>();
        services.AddSingleton(transport ?? new UnusedStreamingTransport());
        services.AddSingleton(docketStore);
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

        public IAsyncEnumerable<TransportMessage> ReceiveAsync(string connectionId, CancellationToken ct)
            => throw new InvalidOperationException("UnusedStreamingTransport.ReceiveAsync should not be called");

        public Task<T> AwaitEventAsync<T>(string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException("UnusedStreamingTransport.AwaitEventAsync should not be called");
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

        public IAsyncEnumerable<TransportMessage> ReceiveAsync(string connectionId, CancellationToken ct)
            => throw new InvalidOperationException("RecordingStreamingTransport.ReceiveAsync should not be called");

        public Task<T> AwaitEventAsync<T>(string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException("RecordingStreamingTransport.AwaitEventAsync should not be called");
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
