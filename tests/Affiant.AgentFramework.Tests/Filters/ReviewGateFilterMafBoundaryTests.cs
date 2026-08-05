namespace Affiant.AgentFramework.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.AgentFramework.Extensions;
using Affiant.AgentFramework.Filters;
using Affiant.AgentFramework.Tests.Utilities;
using Affiant.Core.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// P5a — MAF adapter half (item 4 of this wave). <c>Affiant.Core.Filters.ReviewGateFilter</c> is a
/// neutral <c>ICompletionStageFilter</c>: it is registered exactly once, by the shared
/// <c>Affiant.Core.Extensions.ServiceCollectionExtensions.AddAffiantCompletionFilters()</c> helper,
/// which BOTH <c>AddAffiantSkFilters()</c> (SK) and <c>AddAffiantAgentFramework()</c> (MAF) call.
/// There is no MAF-specific copy of the filing/termination logic to write — the P5a rewrite in item
/// 4 of this wave applies to MAF for free, through <see cref="AffiantFunctionInvocationMiddleware"/>'s
/// single onion, the moment a write tool's JSON is a properly $type-discriminated
/// <see cref="WriteProposal"/> (the framework's documented tool-authoring contract,
/// docs/tool-authoring-guide.md: "Always serialize the return value with .ToJsonString()").
///
/// <para>
/// <b>The one real boundary (evidence: area-4 d1-host-bypass.md finding B.5, d1-fw-intent.md
/// finding D) is NOT in this framework repo.</b> Meridian's specific MAF write tools (host code,
/// `/home/seevali/worktrees/affiant-host-apps/a4-recon`, read-only reference) emit plain
/// <c>{"requiresConfirmation": true, ...}</c> JSON with no <c>$type</c> discriminator — a deviation
/// from the documented contract, not a structural MAF limitation. <c>ReviewGateFilterMafNonConformingPayloadPinTest</c>
/// below pins exactly that failure mode as a fact (JsonSerializer.Deserialize&lt;ToolEnvelope&gt;
/// throws NotSupportedException for a $type-less object, caught by ReviewGateFilter's existing
/// catch clause and treated as "not a WriteProposal" — silent skip, matching Meridian's observed
/// behavior exactly) so a reader can verify the boundary is host non-conformance, not something
/// this wave's rewrite could fix from the framework side. Nothing in <c>Affiant.AgentFramework</c>
/// (checked at fc46b95: <c>AffiantToolCatalog</c>, <c>AgentExtensions.WithAffiant</c>,
/// <c>AffiantFunctionInvocationMiddleware</c>) constrains what a host's own <c>AIFunction</c> body
/// returns — that return value is 100% host-authored, on both adapters equally.
/// </para>
/// </summary>
public class ReviewGateFilterMafBoundaryTests
{
    [Fact]
    public async Task ConformingWriteProposal_RequiresReview_EndsTurn_ThroughOnlyPublicAddAffiantAgentFramework()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Host-owned pieces only — no default framework implementation exists for any of these.
        // IChatClient is required because AddAffiantAgentFramework() registers
        // AgentFrameworkInferenceCompletionPort (resolves IChatClient) behind InferenceTriggerFilter,
        // a registered IToolInvocationFilter — unrelated to this test, but part of the same public
        // extension chain, exactly as a real host wires it.
        services.AddSingleton<IStreamingTransport>(new NoOpTransport());
        services.AddSingleton<IDocketStore>(new InMemoryDocketStore());
        services.AddSingleton<IReviewContextProvider>(new ConstantReviewContextProvider(BuildReviewContext()));
        services.AddSingleton<IChatClient>(new NoOpChatClient());
        // Unlike SK (where the inference stack is a separate AddAffiantInferenceOrchestration()
        // call), AddAffiantAgentFramework() registers IAffidavitProjection unconditionally in the
        // same call — its default SchemaDrivenAffidavitProjection needs ITaskInferenceStrategy
        // resolvable for ValidateOnBuild below, even though this test never exercises inference.
        services.AddSingleton<ITaskInferenceStrategy>(new StubTaskInferenceStrategy());
        // AddAffiantCore() also registers UiGuidanceBridge (area-4 P1f(b)), which needs
        // IRouteRegistry resolvable for ValidateOnBuild below, even though this test never
        // exercises guidance.
        services.AddSingleton<IRouteRegistry>(new NoOpRouteRegistry());

        // Public Add* extension chain only — the same one item 4's SK ordering test uses,
        // AddAffiantAgentFramework() instead of AddAffiantSemanticKernel(). No MAF-specific filing
        // filter is registered anywhere — ReviewGateFilter comes from AddAffiantCompletionFilters()
        // inside AddAffiantAgentFramework() itself.
        services.AddAffiantCore(o => o.EnableObservability = false);
        services.AddAffiantAgentFramework();

        var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        using var scope = sp.CreateScope();
        var pipeline = sp.GetRequiredService<Affiant.Core.Services.ToolInvocationPipeline>();
        var middleware = new AffiantFunctionInvocationMiddleware(pipeline, new StubRegistry());

        // Properly $type-discriminated — the documented contract every tool-authoring guide example
        // follows (docs/tool-authoring-guide.md: "Always serialize ... with .ToJsonString()").
        var proposalJson = new WriteProposal(
            "DoWrite", DateTimeOffset.UtcNow, new { field = "value" }).ToJsonString();

        var function = AIFunctionFactory.Create(() => "unused", name: "DoWrite");
        var context = new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments { Services = scope.ServiceProvider },
            Messages = new List<ChatMessage>(),
        };
        var stubAgent = new ChatClientAgent(new NoOpChatClient(), instructions: "stub");

        var result = await middleware.InvokeAsync(
            stubAgent, context, (_, _) => new ValueTask<object?>(proposalJson), CancellationToken.None);

        Assert.True(context.Terminate, "RequiresReview must end the turn on MAF too — same neutral filter as SK.");
        var resultText = Assert.IsType<string>(result);
        Assert.Contains("filed for review", resultText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName =
        "PINNED BOUNDARY FACT: a $type-less WriteProposal-shaped JSON (Meridian's actual host-code shape, " +
        "read-only reference) is silently skipped by ReviewGateFilter — host non-conformance, not a framework gap")]
    public async Task NonConformingPayload_NoTypeDiscriminator_IsSilentlySkipped_NotAFrameworkBug()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStreamingTransport>(new NoOpTransport());
        services.AddSingleton<IDocketStore>(new InMemoryDocketStore());
        services.AddSingleton<IReviewContextProvider>(new ConstantReviewContextProvider(BuildReviewContext()));
        services.AddSingleton<IChatClient>(new NoOpChatClient());
        services.AddAffiantCore(o => o.EnableObservability = false);
        services.AddAffiantAgentFramework();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var pipeline = sp.GetRequiredService<Affiant.Core.Services.ToolInvocationPipeline>();
        var middleware = new AffiantFunctionInvocationMiddleware(pipeline, new StubRegistry());

        // Mirrors Meridian's chat-loop-spec.md §0.2-documented shape exactly: a plain object with a
        // requiresConfirmation flag and no $type — never a legal ToolEnvelope discriminator.
        const string nonConformingJson = """{"requiresConfirmation":true,"toolName":"DoWrite"}""";

        var function = AIFunctionFactory.Create(() => "unused", name: "DoWrite");
        var context = new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments { Services = scope.ServiceProvider },
            Messages = new List<ChatMessage>(),
        };
        var stubAgent = new ChatClientAgent(new NoOpChatClient(), instructions: "stub");

        var result = await middleware.InvokeAsync(
            stubAgent, context, (_, _) => new ValueTask<object?>(nonConformingJson), CancellationToken.None);

        // Silently skipped — ReviewGateFilter's own JsonException/NotSupportedException catch clause
        // treats "not deserializable as a discriminated ToolEnvelope" as "not a WriteProposal", by
        // design (the same clause every other envelope-shape probe in this filter relies on). Never
        // filed, never terminated, the model sees its own raw JSON back untouched.
        Assert.False(context.Terminate);
        Assert.Equal(nonConformingJson, result);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ReviewContext BuildReviewContext() => new(
        SessionId: "session-maf-boundary",
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

    private sealed class StubTaskInferenceStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "StubEntity";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class NoOpRouteRegistry : IRouteRegistry
    {
        public void Register(GuidableElement element) { }
        public IReadOnlyList<GuidableElement> GetElementsForRoute(string route) => [];
        public IReadOnlyList<GuidableElement> GetAllElements() => [];
        public GuidableElement? GetElementById(string elementId) => null;
    }

    private sealed class StubRegistry : IAffiantToolRegistry
    {
        public void Register(AffiantToolDescriptor descriptor) { }
        public AffiantToolDescriptor? Find(string functionName, string? pluginName = null) => null;
        public IReadOnlyList<AffiantToolDescriptor> All => [];
    }

    private sealed class ConstantReviewContextProvider(ReviewContext context) : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => context;
    }

    private sealed class NoOpTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct) =>
            Task.CompletedTask;
        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default) =>
            Task.FromCanceled<EvidenceCardResponse>(ct);
    }

    private sealed class InMemoryDocketStore : IDocketStore
    {
        private readonly Dictionary<Guid, DocketEntry> _entries = [];

        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<ConversationContext?>(null);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
        {
            _entries[entry.EntryId] = entry;
            return Task.CompletedTask;
        }

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct) =>
            Task.FromResult(_entries.TryGetValue(entryId, out var e) ? e : null);

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
        {
            if (!_entries.TryGetValue(entryId, out var existing) || existing.Status != ReviewStatus.Pending)
                return Task.FromResult(0);
            _entries[entryId] = existing with { Status = status };
            return Task.FromResult(1);
        }

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
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

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => Task.FromResult(_entries.Values.FirstOrDefault(e => e.ResubmittedTo == entryId));

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
