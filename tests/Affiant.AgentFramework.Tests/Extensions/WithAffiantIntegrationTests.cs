namespace Affiant.AgentFramework.Tests.Extensions;

using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.AgentFramework.Extensions;
using Affiant.AgentFramework.Tests.Utilities;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// End-to-end tests driving a real <see cref="ChatClientAgent"/> wrapped by
/// <see cref="AgentExtensions.WithAffiant"/> through a scripted single-tool-call round trip.
/// Covers: LLM-supplied argument capture reaching <see cref="ContextFabric"/>, and a
/// <see cref="WriteProposal"/> envelope routing through <see cref="ReviewGate"/>.
/// </summary>
public class WithAffiantIntegrationTests
{
    [Fact]
    public async Task ArgumentCapture_ReachesContextFabric()
    {
        var docketStore = new FakeDocketStore();
        var scriptedClient = new ScriptedChatClient(
            "CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        var services = BuildServices(docketStore, scriptedClient);
        var sp = services.BuildServiceProvider();

        var catalog = AffiantToolCatalog.FromType<WidgetTools>();

        var agent = new ChatClientAgent(scriptedClient, instructions: "test", tools: catalog.Functions.Cast<AITool>().ToList(), services: sp)
            .WithAffiant(sp, catalog);

        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("please create a widget", session);

        var fabric = sp.GetRequiredService<ContextFabric>();
        var chain = fabric.GetFieldChain("name");

        Assert.NotNull(chain);
        Assert.Equal(ProvenanceSource.Conversation, chain.Current.Source);
        Assert.Contains("CreateWidget", chain.Current.Evidence);
    }

    [Fact]
    public async Task WriteProposalEnvelope_RoutesThroughReviewGate()
    {
        var docketStore = new FakeDocketStore();
        var scriptedClient = new ScriptedChatClient(
            "CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        var services = BuildServices(docketStore, scriptedClient);
        var sp = services.BuildServiceProvider();

        var catalog = AffiantToolCatalog.FromType<WidgetTools>();

        var agent = new ChatClientAgent(scriptedClient, instructions: "test", tools: catalog.Functions.Cast<AITool>().ToList(), services: sp)
            .WithAffiant(sp, catalog);

        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("please create a widget", session);

        Assert.Single(docketStore.Filed);
        Assert.Equal(ReviewStatus.Approved, docketStore.Filed[0].Status); // StandingOrderPolicy auto-approves
        Assert.Equal("CreateWidget", docketStore.Filed[0].OperationType);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private sealed class WidgetTools
    {
        [AffiantWriteTool("WriteCreate", "Widget", typeof(FakeStrategy))]
        public string CreateWidget(string name)
        {
            var affidavit = new Affidavit(
                OperationType: "create",
                EntityType: "Widget",
                EntityId: null,
                Fields: [new AffidavitField(
                    "name", name, null, ProvenanceChain.From(ProvenanceTag.FromTool("CreateWidget")))],
                AggregateConfidence: 0.9f,
                Warnings: [],
                RequiresConfirmation: false);

            var proposal = new WriteProposal("CreateWidget", DateTimeOffset.UtcNow, affidavit);
            return proposal.ToJsonString();
        }
    }

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }

    // The neutral ReviewGateFilter (shared with the SK backend) deserializes WriteProposal.Envelope
    // as a plain object via System.Text.Json, which yields a JsonElement, not the original CLR
    // Affidavit — matching the SK-side ReviewGateFilterTests fixture, this provider supplies a
    // constant ReviewContext rather than attempting to cast the round-tripped Envelope.
    private static ReviewContext BuildReviewContext() => new(
        SessionId: "session-test",
        TenantId: "tenant-test",
        UserId: "user-test",
        ReviewerUserId: "reviewer-test",
        Affidavit: new Affidavit(
            OperationType: "create",
            EntityType: "Widget",
            EntityId: null,
            Fields: [],
            AggregateConfidence: 1.0f,
            Warnings: [],
            RequiresConfirmation: false));

    private static IServiceCollection BuildServices(FakeDocketStore docketStore, IChatClient chatClient)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WidgetTools());
        services.AddSingleton<FakeStrategy>();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddAffiantAgentFramework();
        // AgentFrameworkInferenceCompletionPort resolves IChatClient — required because WidgetTools
        // is a registered write-intent tool, so InferenceTriggerFilter constructs TaskInferenceRunner
        // (and therefore the port) merely by being resolved from the filter enumerable.
        services.AddSingleton(chatClient);

        services.AddScoped<ReviewGate>();
        services.AddSingleton<IStreamingTransport>(new UnusedStreamingTransport());
        services.AddSingleton<IDocketStore>(docketStore);
        services.AddSingleton<IApprovalPolicy>(new StandingOrderPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();
        services.AddSingleton<IReviewContextProvider>(new DelegatingReviewContextProvider(
            _ => BuildReviewContext()));

        return services;
    }

    private sealed class DelegatingReviewContextProvider(Func<WriteProposal, ReviewContext?> build)
        : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => build(proposal);
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

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
            => Task.CompletedTask;
    }
}
