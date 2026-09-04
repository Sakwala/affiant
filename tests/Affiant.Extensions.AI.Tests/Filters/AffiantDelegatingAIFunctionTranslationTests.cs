namespace Affiant.Extensions.AI.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Filters;
using Affiant.Extensions.AI.Tests.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// What <see cref="AffiantDelegatingAIFunction"/> translates between Microsoft.Extensions.AI's call
/// shape and the neutral <c>ToolInvocationRequest</c>, driven directly against the wrapper — no chat
/// loop. The counterpart of
/// <c>tests/Affiant.AgentFramework.Tests/Filters/AffiantFunctionInvocationMiddlewareTests.cs</c>, for
/// the cases that file covers and this package's seam tests did not yet: plugin-name resolution
/// through <see cref="IAffiantToolRegistry"/>, argument sharing by reference, and the review-filing
/// failure that must reach the model as a typed <see cref="ToolError"/> rather than as the unfiled
/// proposal.
///
/// <para>
/// Terminate mapping and conversation identity — the rest of that MAF file's coverage — live in
/// <see cref="AffiantDelegatingAIFunctionContextTests"/>, because at this seam they are properties of
/// the wrapper's dialogue with <c>FunctionInvokingChatClient.CurrentContext</c> rather than of a
/// context object handed in as a parameter.
/// </para>
/// </summary>
public class AffiantDelegatingAIFunctionTranslationTests
{
    // ── Plugin-name resolution via the registry ──────────────────────────────

    [Fact]
    public async Task PluginName_ResolvedFromRegistry_ByFunctionName()
    {
        var observed = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new PluginNameRecordingFilter(observed));
        var sp = services.BuildServiceProvider();

        var registry = new StubRegistry();
        registry.Register(new AffiantToolDescriptor(
            "Widgets_Create", "WidgetPlugin", Operation.WriteCreate, "Widget", null));

        var wrapped = new AffiantDelegatingAIFunction(
            AIFunctionFactory.Create(() => "raw", name: "Widgets_Create"),
            new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>()),
            registry);

        await wrapped.InvokeAsync(new AIFunctionArguments { Services = sp });

        Assert.Equal(["WidgetPlugin"], observed);
    }

    /// <summary>
    /// An unregistered tool still runs — it simply carries an empty plugin name into the neutral
    /// context. Matching the Microsoft Agent Framework bridge exactly: refusing to invoke a tool
    /// nobody declared would make Affiant a gate on tool existence, which is not its job.
    /// </summary>
    [Fact]
    public async Task PluginName_EmptyString_WhenNoDescriptorRegistered()
    {
        var observed = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new PluginNameRecordingFilter(observed));
        var sp = services.BuildServiceProvider();

        var wrapped = new AffiantDelegatingAIFunction(
            AIFunctionFactory.Create(() => "raw", name: "Unregistered"),
            new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>()),
            new StubRegistry());

        await wrapped.InvokeAsync(new AIFunctionArguments { Services = sp });

        Assert.Equal([""], observed);
    }

    // ── Arguments shared by reference ────────────────────────────────────────

    /// <summary>
    /// The neutral context's <c>Arguments</c> is the very same
    /// <see cref="AIFunctionArguments"/> instance the caller supplied and the tool is invoked with —
    /// not a copy. That is what makes a pre-invocation filter's mutation load-bearing: the tool
    /// receives the amended value. A defensive copy anywhere along this path would turn every
    /// argument-rewriting filter into a silent no-op.
    /// </summary>
    [Fact]
    public async Task Arguments_SharedByReference_SoAPreToolFilterMutationReachesTheTool()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ArgumentRewritingFilter("name", "mutated"));
        var sp = services.BuildServiceProvider();

        string? seenByTool = null;
        var wrapped = new AffiantDelegatingAIFunction(
            AIFunctionFactory.Create((string name) =>
            {
                seenByTool = name;
                return "ok";
            }, name: "EchoArgs"),
            new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>()),
            new StubRegistry());

        var arguments = new AIFunctionArguments(
            new Dictionary<string, object?> { ["name"] = "original" }) { Services = sp };

        await wrapped.InvokeAsync(arguments);

        Assert.Equal("mutated", seenByTool);
        Assert.Equal("mutated", arguments["name"]);
    }

    // ── Review-filing failure ────────────────────────────────────────────────

    /// <summary>
    /// affiant#22 / FV-9 at this seam. A docket store that throws means the proposal was never
    /// durably filed, so the model must be told that in the framework's typed error shape — never
    /// handed back its own proposal JSON, which reads to a model as "filed, awaiting review".
    ///
    /// <para>
    /// Like the Microsoft Agent Framework's single onion, this seam has no separate completion stage
    /// to fall through: <c>ReviewGateFilter</c> rewrites <c>context.Result</c> and this wrapper
    /// returns it directly.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReviewGateFiling_Throws_WrapperReturnsTypedToolError_NotTheRawProposal()
    {
        const string WriteProposalJson =
            """{"$type":"write","toolName":"DoWrite","timestamp":"2026-01-01T00:00:00Z","envelope":null}""";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ReviewGate>();
        services.AddSingleton(new AffiantCoreOptions());
        services.AddSingleton<IDocketStore>(new ThrowingDocketStore());
        services.AddSingleton<IStreamingTransport>(new RecordingStreamingTransport());
        services.AddSingleton<IApprovalPolicy>(new ReviewerConfirmationPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();
        services.AddSingleton<IReviewContextProvider>(new ConstantReviewContextProvider(BuildReviewContext()));
        services.AddSingleton<IToolInvocationFilter>(new ReviewGateFilter(NullLogger<ReviewGateFilter>.Instance));
        var sp = services.BuildServiceProvider();

        var wrapped = new AffiantDelegatingAIFunction(
            AIFunctionFactory.Create(() => WriteProposalJson, name: "DoWrite"),
            new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>()),
            new StubRegistry());

        var result = await wrapped.InvokeAsync(new AIFunctionArguments { Services = sp });

        var resultJson = Assert.IsType<string>(result);
        Assert.Contains("REVIEW_FILING_FAILED", resultJson);
        Assert.Contains("\"$type\":\"error\"", resultJson);
        Assert.DoesNotContain("\"$type\":\"write\"", resultJson); // not the raw, unfiled proposal
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private static ReviewContext BuildReviewContext() => new(
        SessionId: "session-extensions-ai-test",
        TenantId: "tenant-default",
        UserId: "user-123",
        ReviewerUserId: "reviewer-456",
        Affidavit: new Affidavit(
            OperationType: "DoWrite",
            EntityType: "TestEntity",
            EntityId: null,
            Fields: [],
            AggregateConfidence: 1.0f,
            Warnings: [],
            RequiresConfirmation: true));

    private sealed class PluginNameRecordingFilter(List<string> observed) : IToolInvocationFilter
    {
        public async Task OnToolInvocationAsync(
            ToolInvocationContext context,
            Func<ToolInvocationContext, Task> next,
            CancellationToken cancellationToken = default)
        {
            observed.Add(context.PluginName);
            await next(context).ConfigureAwait(false);
        }
    }

    private sealed class ArgumentRewritingFilter(string key, object? value) : IToolInvocationFilter
    {
        public async Task OnToolInvocationAsync(
            ToolInvocationContext context,
            Func<ToolInvocationContext, Task> next,
            CancellationToken cancellationToken = default)
        {
            context.Arguments[key] = value;
            await next(context).ConfigureAwait(false);
        }
    }

    private sealed class ConstantReviewContextProvider(ReviewContext context) : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => context;
    }

    private sealed class StubRegistry : IAffiantToolRegistry
    {
        private readonly List<AffiantToolDescriptor> _descriptors = [];

        public void Register(AffiantToolDescriptor descriptor) => _descriptors.Add(descriptor);

        public AffiantToolDescriptor? Find(string functionName, string? pluginName = null) =>
            _descriptors.FirstOrDefault(d => d.FunctionName == functionName
                && (pluginName is null || d.PluginName == pluginName));

        public IReadOnlyList<AffiantToolDescriptor> All => _descriptors;
    }

    /// <summary>Docket store whose very first durable write fails — a pre-persist filing failure.</summary>
    private sealed class ThrowingDocketStore : IDocketStore
    {
        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct) =>
            throw new InvalidOperationException("docket store is down");

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct) =>
            Task.FromResult<DocketEntry?>(null);

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct) =>
            throw new InvalidOperationException("docket store is down");

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct) =>
            throw new InvalidOperationException("docket store is down");

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct) =>
            throw new InvalidOperationException("docket store is down");

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct) =>
            Task.FromResult<DocketEntry?>(null);

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<ConversationContext?>(null);

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct) => Task.CompletedTask;
    }
}
