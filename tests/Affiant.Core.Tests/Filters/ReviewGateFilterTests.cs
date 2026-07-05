namespace Affiant.Core.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
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

    private ServiceCollection BuildReviewGateStack(FakeDocketStore docketStore)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);

        services.AddScoped<ReviewGate>();
        services.AddSingleton<IStreamingTransport>(new UnusedStreamingTransport());
        services.AddSingleton<IDocketStore>(docketStore);
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
