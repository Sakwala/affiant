namespace Affiant.SemanticKernel.Tests.Filters;

using System.Runtime.CompilerServices;
using System.Threading;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Extensions;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Integration tests for ReviewGateFilter (IAutoFunctionInvocationFilter position 5).
///
/// Covers:
///   1. Graceful degradation — no-op when IReviewContextProvider or ReviewGate not registered.
///   2. Non-WriteProposal results — filter is a no-op and passes through unchanged.
///   3. Scope-per-invocation — a new DI scope is opened for each WriteProposal result.
///   4. WriteProposal → ReviewGate path — proposal is routed through ReviewGate.FileReviewAsync.
/// </summary>
public class ReviewGateFilterTests
{
    // ── Graceful degradation ─────────────────────────────────────────────────

    [Fact]
    public async Task ReviewGateFilter_NoReviewContextProvider_IsNoOp()
    {
        // ReviewGateFilter is registered but IReviewContextProvider is not.
        // The filter must silently skip without throwing.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskInferenceStrategy, NoFieldStrategy>();
        services.AddScoped<ContextFabric>();
        services.AddScoped<TaskInferenceStep>();
        services.AddAffiantSemanticKernel();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddSingleton<IChatCompletionService>(
            new FakeLlmProvider("WritePlugin", "DoWrite"));
        services.AddKernel();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();

        var writeProposalJson = BuildWriteProposalJson("DoWrite");
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("WritePlugin",
            [KernelFunctionFactory.CreateFromMethod(() => writeProposalJson, "DoWrite")]));

        // Must not throw even though no IReviewContextProvider or ReviewGate are registered.
        var ex = await Record.ExceptionAsync(() =>
            kernel.InvokePromptAsync("do write",
                new KernelArguments(new PromptExecutionSettings
                {
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
                })));

        Assert.Null(ex);
    }

    [Fact]
    public async Task ReviewGateFilter_NonWriteProposalResult_IsNoOp()
    {
        // Tool returns a plain string (not a WriteProposal JSON). Filter must be a no-op.
        var services = BuildReviewGateStack(out _, out _);
        services.AddSingleton<IChatCompletionService>(
            new FakeLlmProvider("ReadPlugin", "DoRead"));
        services.AddKernel();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();

        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("ReadPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => "plain text result", "DoRead")]));

        var ex = await Record.ExceptionAsync(() =>
            kernel.InvokePromptAsync("do read",
                new KernelArguments(new PromptExecutionSettings
                {
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
                })));

        Assert.Null(ex);
    }

    // ── Scope-per-invocation ─────────────────────────────────────────────────

    [Fact]
    public async Task ReviewGateFilter_CreatesNewScopePerInvocation()
    {
        // A scoped IReviewContextProvider tracks how many distinct instances were created.
        // Two separate OnAutoFunctionInvocationAsync calls must each open a fresh scope →
        // fresh provider instance. We drive the filter directly via the public
        // AutoFunctionInvocationContext constructor; SK 1.74's auto-invocation loop requires
        // provider-specific metadata a bare IChatCompletionService stub cannot supply.
        var instanceIds = new List<Guid>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskInferenceStrategy, NoFieldStrategy>();
        services.AddScoped<ContextFabric>();
        services.AddScoped<TaskInferenceStep>();
        services.AddAffiantSemanticKernel();
        services.AddAffiantCore(opts => opts.EnableObservability = false);

        // Scoped provider — each new scope creates a new instance with a unique ID
        services.AddScoped<IReviewContextProvider>(_ =>
        {
            var id = Guid.NewGuid();
            instanceIds.Add(id);
            return new ConstantReviewContextProvider(BuildReviewContext());
        });

        services.AddKernel();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();

        var writeProposalJson = BuildWriteProposalJson("DoWrite");
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("WritePlugin",
            [KernelFunctionFactory.CreateFromMethod(() => writeProposalJson, "DoWrite")]));

        var function = kernel.Plugins["WritePlugin"]["DoWrite"];
        var fnResult = await kernel.InvokeAsync("WritePlugin", "DoWrite");
        var chatHistory = new ChatHistory();
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, string.Empty);

        var reviewGateFilter = kernel.AutoFunctionInvocationFilters.OfType<ReviewGateFilter>().Single();

        // First invocation: filter opens scope 1, resolves IReviewContextProvider
        var ctx1 = new AutoFunctionInvocationContext(kernel, function, fnResult, chatHistory, chatMessage);
        await reviewGateFilter.OnAutoFunctionInvocationAsync(ctx1, _ => Task.CompletedTask);

        // Second invocation: filter opens scope 2, resolves IReviewContextProvider again
        var ctx2 = new AutoFunctionInvocationContext(kernel, function, fnResult, chatHistory, chatMessage);
        await reviewGateFilter.OnAutoFunctionInvocationAsync(ctx2, _ => Task.CompletedTask);

        // Two separate scopes → two distinct IReviewContextProvider instances
        Assert.Equal(2, instanceIds.Count);
        Assert.NotEqual(instanceIds[0], instanceIds[1]);
    }

    // ── WriteProposal → ReviewGate path ─────────────────────────────────────

    [Fact]
    public async Task ReviewGateFilter_WriteProposalResult_RoutesToReviewGate()
    {
        // Full path: plugin returns WriteProposal JSON → ReviewGateFilter detects it →
        // calls IReviewContextProvider.BuildReviewContext → calls ReviewGate.FileReviewAsync →
        // StandingOrderPolicy auto-approves → DocketEntry is filed in FakeDocketStore.
        // Filter is driven directly via AutoFunctionInvocationContext; SK 1.74's auto-invocation
        // loop requires provider-specific metadata a bare IChatCompletionService stub cannot supply.
        var docketStore = new FakeDocketStore();

        var services = BuildReviewGateStack(out _, out var contextProvider);
        services.AddSingleton<IDocketStore>(docketStore);
        services.AddKernel();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();

        var writeProposalJson = BuildWriteProposalJson("DoWrite");
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("WritePlugin",
            [KernelFunctionFactory.CreateFromMethod(() => writeProposalJson, "DoWrite")]));

        var function = kernel.Plugins["WritePlugin"]["DoWrite"];
        var fnResult = await kernel.InvokeAsync("WritePlugin", "DoWrite");
        var chatHistory = new ChatHistory();
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, string.Empty);

        var reviewGateFilter = kernel.AutoFunctionInvocationFilters.OfType<ReviewGateFilter>().Single();
        var autoCtx = new AutoFunctionInvocationContext(kernel, function, fnResult, chatHistory, chatMessage);
        await reviewGateFilter.OnAutoFunctionInvocationAsync(autoCtx, _ => Task.CompletedTask);

        // StandingOrderPolicy auto-approves → DocketEntry must have been filed and approved
        Assert.Single(docketStore.Filed);
        Assert.Equal(ReviewStatus.Approved, docketStore.Filed[0].Status);
        Assert.Equal("DoWrite", docketStore.Filed[0].OperationType);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildWriteProposalJson(string toolName) =>
        $$"""{"$type":"write","toolName":"{{toolName}}","timestamp":"2026-01-01T00:00:00Z","envelope":null}""";

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

    private static ServiceCollection BuildReviewGateStack(
        out FakeDocketStore docketStore,
        out ConstantReviewContextProvider contextProvider)
    {
        docketStore = new FakeDocketStore();
        contextProvider = new ConstantReviewContextProvider(BuildReviewContext());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskInferenceStrategy, NoFieldStrategy>();
        services.AddScoped<ContextFabric>();
        services.AddScoped<TaskInferenceStep>();
        services.AddAffiantSemanticKernel();
        services.AddAffiantCore(opts => opts.EnableObservability = false);

        // ReviewGate infrastructure — minimal fakes for the standing-order path
        services.AddScoped<ReviewGate>();
        services.AddSingleton<IStreamingTransport>(new UnusedStreamingTransport());
        services.AddSingleton<IDocketStore>(docketStore);
        services.AddSingleton<IApprovalPolicy>(new StandingOrderPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();

        services.AddSingleton<IReviewContextProvider>(contextProvider);
        return services;
    }

    private sealed class NoFieldStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "NoOp";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }

    /// <summary>IReviewContextProvider that always returns the same pre-built ReviewContext.</summary>
    internal sealed class ConstantReviewContextProvider(ReviewContext context) : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => context;
    }

    /// <summary>IApprovalPolicy that always returns StandingOrder (auto-approve, no UI).</summary>
    private sealed class StandingOrderPolicy : IApprovalPolicy
    {
        public Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
            => Task.FromResult<ReviewRequirement?>(ReviewRequirement.StandingOrder);
    }

    /// <summary>
    /// Minimal IStreamingTransport stub. With StandingOrderPolicy, ReviewGate auto-approves
    /// without calling BroadcastToGroupAsync or AwaitEventAsync.
    /// </summary>
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

    /// <summary>Minimal in-memory IDocketStore for verifying that ReviewGate files the entry.</summary>
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

    /// <summary>
    /// Stateful fake IChatCompletionService that returns a tool call on the first call
    /// and a plain text response on subsequent calls.
    /// </summary>
    private sealed class FakeLlmProvider(
        string pluginName,
        string functionName,
        string callId = "call-rg-1") : IChatCompletionService
    {
        private int _callCount;

        public IReadOnlyDictionary<string, object?> Attributes =>
            new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var count = System.Threading.Interlocked.Increment(ref _callCount);
            ChatMessageContent content = count == 1
                ? new ChatMessageContent(AuthorRole.Assistant,
                    new ChatMessageContentItemCollection
                        { new FunctionCallContent(functionName, pluginName, callId) })
                : new ChatMessageContent(AuthorRole.Assistant, "(done)");

            IReadOnlyList<ChatMessageContent> result = [content];
            return Task.FromResult(result);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, "(done)");
        }
    }

    /// <summary>
    /// FakeLlmProvider that can be reset to re-trigger a tool call on the next invocation.
    /// Used in scope-per-invocation tests that need two separate auto-invocations.
    /// </summary>
    private sealed class TwoCallFakeLlmProvider(
        string pluginName,
        string functionName) : IChatCompletionService
    {
        private int _callCount;

        public IReadOnlyDictionary<string, object?> Attributes =>
            new Dictionary<string, object?>();

        public void Reset() => _callCount = 0;

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var count = System.Threading.Interlocked.Increment(ref _callCount);
            ChatMessageContent content = count == 1
                ? new ChatMessageContent(AuthorRole.Assistant,
                    new ChatMessageContentItemCollection
                        { new FunctionCallContent(functionName, pluginName, $"call-{count}") })
                : new ChatMessageContent(AuthorRole.Assistant, "(done)");

            IReadOnlyList<ChatMessageContent> result = [content];
            return Task.FromResult(result);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, "(done)");
        }
    }
}
