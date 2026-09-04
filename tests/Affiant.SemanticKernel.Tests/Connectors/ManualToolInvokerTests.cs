using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Xunit;

namespace Affiant.SemanticKernel.Tests.Connectors;

public class ManualToolInvokerTests
{
    private static ToolInvocationPipeline EmptyPipeline() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task InvokesRegisteredFunction_AndReturnsResult()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var plugin = KernelPluginFactory.CreateFromFunctions("TestPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => 42, "GetAnswer")]);
        kernel.Plugins.Add(plugin);

        var invoker = new ManualToolInvoker(EmptyPipeline(), NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent(
            functionName: "GetAnswer",
            pluginName: "TestPlugin",
            id: "call-1");

        var result = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        Assert.Equal("42", result.Result?.ToString());
        Assert.Equal("call-1", result.CallId);
    }

    [Fact]
    public async Task ReturnsErrorResult_WhenFunctionNotFound()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var invoker = new ManualToolInvoker(EmptyPipeline(), NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent(
            functionName: "Missing",
            pluginName: "NonExistent",
            id: "call-2");

        var result = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        var resultStr = result.Result?.ToString() ?? string.Empty;
        Assert.Contains("FUNCTION_NOT_FOUND", resultStr);
        Assert.Equal("call-2", result.CallId);
    }

    [Fact]
    public async Task PreservesPluginName_InResult()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var plugin = KernelPluginFactory.CreateFromFunctions("MyPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => "hello", "Greet")]);
        kernel.Plugins.Add(plugin);

        var invoker = new ManualToolInvoker(EmptyPipeline(), NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent(
            functionName: "Greet",
            pluginName: "MyPlugin",
            id: "call-3");

        var result = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        Assert.Equal("MyPlugin", result.PluginName);
        Assert.Equal("Greet", result.FunctionName);
    }

    [Fact]
    public async Task PassesArguments_ToFunction()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var plugin = KernelPluginFactory.CreateFromFunctions("MathPlugin",
            [KernelFunctionFactory.CreateFromMethod((int x) => x * 2, "Double")]);
        kernel.Plugins.Add(plugin);

        var invoker = new ManualToolInvoker(EmptyPipeline(), NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent(
            functionName: "Double",
            pluginName: "MathPlugin",
            id: "call-4",
            arguments: new KernelArguments { ["x"] = "7" });

        var result = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        Assert.Equal("14", result.Result?.ToString());
    }

    /// <summary>
    /// The manual/degraded-provider fallback path bypasses SK's auto-invocation loop, where the
    /// completion segment (merge + review gate) lives. ManualToolInvoker must therefore run the
    /// completion segment itself, so a write tool invoked manually files its WriteProposal for
    /// review exactly once (not zero times, and not twice).
    /// </summary>
    [Fact]
    public async Task ManualInvocation_OfWriteTool_FilesExactlyOneReview()
    {
        var docketStore = new FakeDocketStore();
        var sp = BuildReviewStack(docketStore);

        const string writeProposalJson =
            """{"kind":"write","toolName":"DoWrite","timestamp":"2026-01-01T00:00:00Z","envelope":null}""";

        // Resolve the kernel from a turn scope so kernel.Services (the ambient provider the invoker
        // hands the pipeline) carries the scoped completion filters, fabric, and ReviewGate — this is
        // how real hosts resolve the kernel per request.
        using var scope = sp.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<ToolInvocationPipeline>();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("WritePlugin",
            [KernelFunctionFactory.CreateFromMethod(() => writeProposalJson, "DoWrite")]));

        var invoker = new ManualToolInvoker(pipeline, NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent("DoWrite", "WritePlugin", "call-write-1");

        await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        var filed = Assert.Single(docketStore.Filed);
        Assert.Equal(ReviewStatus.Approved, filed.Status); // StandingOrderPolicy auto-approves
        Assert.Equal("DoWrite", filed.OperationType);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ServiceProvider BuildReviewStack(FakeDocketStore docketStore)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddAffiantCompletionFilters();
        services.AddKernel();

        services.AddScoped<ReviewGate>();
        services.AddSingleton<IStreamingTransport>(new UnusedStreamingTransport());
        services.AddSingleton<IDocketStore>(docketStore);
        services.AddSingleton<IApprovalPolicy>(new StandingOrderPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();
        services.AddSingleton<IReviewContextProvider>(new ConstantReviewContextProvider(BuildReviewContext()));
        return services.BuildServiceProvider();
    }

    private static ReviewContext BuildReviewContext() => new(
        SessionId: "session-test",
        TenantId: "tenant-test",
        UserId: "user-test",
        ReviewerUserId: "reviewer-test",
        Affidavit: new Affidavit(
            OperationType: "create",
            EntityType: "TestEntity",
            EntityId: null,
            // A substantive field: the gate refuses a proposal that swears to nothing (GT-3),
            // so a fixture exercising the filing path has to swear to something.
            Fields: [new AffidavitField("field", "value", null,
                ProvenanceChain.From(ProvenanceTag.FromTool("fixture")))],
            AggregateConfidence: 0.9f,
            PopulatedConfidence: 0.9f,
            EmptyFieldCount: 0,
            Warnings: [],
            RequiresConfirmation: false));

    private sealed class ConstantReviewContextProvider(ReviewContext context) : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => context;
    }

    private sealed class StandingOrderPolicy : IApprovalPolicy
    {
        public Task<ApprovalVerdict?> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
            => Task.FromResult<ApprovalVerdict?>(ReviewRequirement.StandingOrder);
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

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
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

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        // ── The scoped, guarded, paged surface ──────────────────────────────
        // Explicit implementations that refuse: this double exists for a test that never reaches
        // the Docket's decision surface, and a stub that quietly answered would let such a test
        // pass against behaviour nobody wrote.
        /// <summary>
        /// The guarded compare-and-set, over this double's own list. Implemented rather than refused
        /// because the filing path reaches it: a Standing Order's approval and its attestation are one
        /// write, so a double that threw here would make an auto-approving test fail at the filing.
        /// </summary>
        Task<DocketTransitionResult> IDocketStore.TransitionAsync(
            Guid entryId, DocketScope scope, ReviewStatus expected, DocketTransitionPatch patch, CancellationToken ct)
        {
            var idx = Filed.FindIndex(e => e.EntryId == entryId);
            if (idx < 0)
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.NotFound());
            if (Filed[idx].Status != expected)
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.AlreadyDecided());

            Filed[idx] = Filed[idx] with
            {
                Status = patch.Status,
                Execution = patch.Status == ReviewStatus.Approved
                    ? patch.Execution ?? ExecutionOutcome.Unexecuted
                    : null,
                Decision = patch.Decision,
                Amendments = patch.Amendments ?? Filed[idx].Amendments,
                AmendedAffidavit = patch.AmendedAffidavit ?? Filed[idx].AmendedAffidavit,
                Attestation = patch.Attestation ?? Filed[idx].Attestation,
                DecidedAt = patch.DecidedAt ?? Filed[idx].DecidedAt,
            };
            return Task.FromResult<DocketTransitionResult>(
                new DocketTransitionResult.Transitioned(Filed[idx]));
        }

        Task<PreserveAmendmentsResult> IDocketStore.PreserveAmendmentsAsync(
            Guid entryId, DocketScope scope, IReadOnlyDictionary<string, object?> amendments,
            PreservedAct act, CancellationToken ct)
            => throw new NotSupportedException();

        Task<RecordExecutionResult> IDocketStore.RecordExecutionAsync(
            Guid entryId, DocketScope scope, ExecutionOutcome outcome, string? detail,
            ExecutionOutcome expected, CancellationToken ct)
            => throw new NotSupportedException();

        Task<RecordSupersessionResult> IDocketStore.RecordSupersessionAsync(
            Guid entryId, DocketScope scope, Guid supersededBy, CancellationToken ct)
            => throw new NotSupportedException();

        Task<int> IDocketStore.MarkBlockedAsync(Guid entryId, BlockedMarker marker, CancellationToken ct)
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
}
