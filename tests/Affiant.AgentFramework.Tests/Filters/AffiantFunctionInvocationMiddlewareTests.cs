namespace Affiant.AgentFramework.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.AgentFramework.Filters;
using Affiant.AgentFramework.Tests.Utilities;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Unit tests for <see cref="AffiantFunctionInvocationMiddleware"/> driven directly against a
/// manually constructed <see cref="FunctionInvocationContext"/> — no real agent round trip.
/// Covers evidence sealing by return value (proposal §2), terminate mapping, and plugin-name
/// resolution via <see cref="IAffiantToolRegistry"/>.
/// </summary>
public class AffiantFunctionInvocationMiddlewareTests
{
    private static readonly AIAgent StubAgent = new ChatClientAgent(new NoOpChatClient(), instructions: "stub");

    private static ToolInvocationPipeline Pipeline(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>());

    private static FunctionInvocationContext BuildContext(AIFunction function, object? initialArgValue = null)
    {
        var arguments = new AIFunctionArguments();
        if (initialArgValue is not null) arguments["x"] = initialArgValue;

        return new FunctionInvocationContext
        {
            Function = function,
            Arguments = arguments,
            Messages = new List<ChatMessage>(),
        };
    }

    private static AIFunction MakeFunction(string name, Func<string> body) =>
        AIFunctionFactory.Create(body, name: name);

    // ── Sealing by return value ─────────────────────────────────────────────

    [Fact]
    public async Task NoFilterReplacesResult_ReturnsToolsRawValue()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("Passthrough", () => "raw");
        var context = BuildContext(function);

        var result = await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.Equal("raw", result);
    }

    [Fact]
    public async Task FilterReplacesResult_ReturnedValueIsTheReplacement()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ReplacingFilter("replaced"));
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("Replaced", () => "raw");
        var context = BuildContext(function);

        var result = await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.Equal("replaced", result);
    }

    // ── Terminate mapping ────────────────────────────────────────────────────

    [Fact]
    public async Task FilterSetsTerminate_MapsOntoFunctionInvocationContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new TerminatingFilter());
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("Terminating", () => "raw");
        var context = BuildContext(function);
        Assert.False(context.Terminate);

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.True(context.Terminate);
    }

    [Fact]
    public async Task NoFilterSetsTerminate_StaysFalse()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("NonTerminating", () => "raw");
        var context = BuildContext(function);

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.False(context.Terminate);
    }

    // ── P1a: ReviewGateFilter filing failure reaches the model on the MAF onion ──
    // (affiant#22 / FV-9, area-3 item 4 adapter reality check: MAF runs every neutral filter in
    // one onion, so ReviewGateFilter rewriting context.Result on a filing failure is returned
    // directly by this middleware — no separate completion-stage seam to fall through, unlike SK.)

    [Fact]
    public async Task ReviewGateFilter_FilingThrows_MiddlewareReturnsTypedToolError_NotTheRawProposal()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ReviewGate>();
        services.AddSingleton(new AffiantCoreOptions());
        services.AddSingleton<IDocketStore>(new ThrowingDocketStore());
        services.AddSingleton<IStreamingTransport>(new RecordingStreamingTransport());
        services.AddSingleton<IApprovalPolicy>(new StandingOrderPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();
        services.AddSingleton<IReviewContextProvider>(new ConstantReviewContextProvider(BuildReviewContext()));
        services.AddSingleton<IToolInvocationFilter>(
            new ReviewGateFilter(NullLogger<ReviewGateFilter>.Instance));
        var sp = services.BuildServiceProvider();

        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());
        var writeProposalJson =
            """{"kind":"write","toolName":"DoWrite","timestamp":"2026-01-01T00:00:00Z","envelope":null}""";
        var function = MakeFunction("DoWrite", () => writeProposalJson);
        var context = BuildContext(function);

        var result = await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>(writeProposalJson), CancellationToken.None);

        var resultJson = Assert.IsType<string>(result);
        Assert.Contains("REVIEW_FILING_FAILED", resultJson);
        Assert.Contains("\"kind\":\"error\"", resultJson);
        Assert.DoesNotContain("\"kind\":\"write\"", resultJson); // not the raw, unfiled proposal
    }

    private static ReviewContext BuildReviewContext() => new(
        SessionId: "session-maf-test",
        TenantId: "tenant-default",
        UserId: "user-123",
        ReviewerUserId: "reviewer-456",
        Affidavit: new Affidavit(
            OperationType: "DoWrite",
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
            RequiresConfirmation: true));

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

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult<DocketEntry?>(null);

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        // ── The scoped, guarded, paged surface ──────────────────────────────
        // Explicit implementations that refuse: this double exists for a test that never reaches
        // the Docket's decision surface, and a stub that quietly answered would let such a test
        // pass against behaviour nobody wrote.
        Task<DocketTransitionResult> IDocketStore.TransitionAsync(
            Guid entryId, DocketScope scope, ReviewStatus expected, DocketTransitionPatch patch, CancellationToken ct)
            => throw new NotSupportedException();

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

    private sealed class RecordingStreamingTransport : IStreamingTransport
    {
        public List<(string GroupId, TransportEvent EventType, object Payload)> Broadcasts { get; } = [];

        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new InvalidOperationException("should not be called");

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        {
            Broadcasts.Add((groupId, eventType, payload));
            return Task.CompletedTask;
        }

        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException("should not be called");
    }

    // ── Plugin-name resolution via registry ─────────────────────────────────

    [Fact]
    public async Task PluginName_ResolvedFromRegistry_ByFunctionName()
    {
        var services = new ServiceCollection();
        var observed = new List<string>();
        services.AddSingleton<IToolInvocationFilter>(new RecordingFilter(observed));
        var sp = services.BuildServiceProvider();

        var registry = new StubRegistry();
        registry.Register(new AffiantToolDescriptor("Widgets_Create", "WidgetPlugin", Operation.WriteCreate, "Widget", null));

        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), registry);
        var function = MakeFunction("Widgets_Create", () => "raw");
        var context = BuildContext(function);

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.Equal(["WidgetPlugin"], observed);
    }

    [Fact]
    public async Task PluginName_EmptyString_WhenNoDescriptorRegistered()
    {
        var services = new ServiceCollection();
        var observed = new List<string>();
        services.AddSingleton<IToolInvocationFilter>(new RecordingFilter(observed));
        var sp = services.BuildServiceProvider();

        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());
        var function = MakeFunction("Unregistered", () => "raw");
        var context = BuildContext(function);

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.Equal([""], observed);
    }

    // ── Conversation identity ────────────────────────────────────────────────

    [Fact]
    public async Task ConversationId_ThreadedFromChatOptions_OntoNeutralContext()
    {
        var services = new ServiceCollection();
        var observed = new List<string?>();
        services.AddSingleton<IToolInvocationFilter>(new ConversationRecordingFilter(observed));
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("WriteThing", () => "raw");
        var context = BuildContext(function);
        context.Options = new ChatOptions { ConversationId = "conversation-42" };

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        // The neutral pipeline saw the run's conversation id — this is what gives InferenceTriggerFilter
        // a genuinely per-conversation idempotency namespace instead of the fabric-hash fallback.
        Assert.Equal(["conversation-42"], observed);
    }

    [Fact]
    public async Task ConversationId_Null_WhenNoChatOptionsConversationId()
    {
        var services = new ServiceCollection();
        var observed = new List<string?>();
        services.AddSingleton<IToolInvocationFilter>(new ConversationRecordingFilter(observed));
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("WriteThing", () => "raw");
        var context = BuildContext(function);

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.Equal([null], observed);
    }

    // ── Arguments shared by reference ────────────────────────────────────────

    [Fact]
    public async Task Arguments_SharedByReference_WithMafArguments()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("EchoArgs", () => "raw");
        var context = BuildContext(function, initialArgValue: "hello");

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        // The neutral pipeline read the same AIFunctionArguments instance MAF supplied.
        Assert.Equal("hello", context.Arguments["x"]);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class StubRegistry : IAffiantToolRegistry
    {
        private readonly List<AffiantToolDescriptor> _descriptors = [];
        public void Register(AffiantToolDescriptor descriptor) => _descriptors.Add(descriptor);

        public AffiantToolDescriptor? Find(string functionName, string? pluginName = null) =>
            _descriptors.FirstOrDefault(d => d.FunctionName == functionName
                && (pluginName is null || d.PluginName == pluginName));

        public IReadOnlyList<AffiantToolDescriptor> All => _descriptors;
    }

    private sealed class ReplacingFilter(object replacement) : IToolInvocationFilter
    {
        public async Task OnToolInvocationAsync(ToolInvocationContext context, Func<ToolInvocationContext, Task> next, CancellationToken cancellationToken = default)
        {
            await next(context);
            context.Result = replacement;
        }
    }

    private sealed class TerminatingFilter : IToolInvocationFilter
    {
        public async Task OnToolInvocationAsync(ToolInvocationContext context, Func<ToolInvocationContext, Task> next, CancellationToken cancellationToken = default)
        {
            await next(context);
            context.Terminate = true;
        }
    }

    private sealed class RecordingFilter(List<string> observed) : IToolInvocationFilter
    {
        public Task OnToolInvocationAsync(ToolInvocationContext context, Func<ToolInvocationContext, Task> next, CancellationToken cancellationToken = default)
        {
            observed.Add(context.PluginName);
            return next(context);
        }
    }

    private sealed class ConversationRecordingFilter(List<string?> observed) : IToolInvocationFilter
    {
        public Task OnToolInvocationAsync(ToolInvocationContext context, Func<ToolInvocationContext, Task> next, CancellationToken cancellationToken = default)
        {
            observed.Add(context.ConversationId);
            return next(context);
        }
    }
}
