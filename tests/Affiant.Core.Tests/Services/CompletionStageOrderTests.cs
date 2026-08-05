namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Guards the completion-stage ordering invariant (framework spec §3.12.4): when both
/// <c>TaskInferenceMergeFilter</c> and <c>ReviewGateFilter</c> run, the merge must <em>complete</em>
/// before the review is filed, so the reviewer sees a fully-merged Affidavit. Both filters do all
/// their work after <c>await next()</c>, so this depends entirely on onion entry order —
/// <c>AddAffiantCompletionFilters()</c> (the single source of truth both backends call) must enter
/// <c>ReviewGateFilter</c> outer and <c>TaskInferenceMergeFilter</c> inner. Fails on the inverted
/// registration (review observed before merge), passes after.
/// </summary>
public sealed class CompletionStageOrderTests
{
    [Fact]
    public async Task CompletionStage_MergeCompletesBeforeReviewIsFiled()
    {
        var events = new List<string>();
        var docketStore = new FakeDocketStore();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddAffiantCompletionFilters();

        services.AddSingleton(new RecordingStrategy(events));
        services.AddScoped<ReviewGate>();
        services.AddSingleton<IStreamingTransport>(new UnusedStreamingTransport());
        services.AddSingleton<IDocketStore>(docketStore);
        services.AddSingleton<IApprovalPolicy>(new StandingOrderPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();
        services.AddSingleton<IReviewContextProvider>(
            new RecordingReviewContextProvider(events, BuildReviewContext()));

        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IAffiantToolRegistry>().Register(new AffiantToolDescriptor(
            "DoWrite", "WritePlugin", new Operation("WriteCreate"), "SpyEntity", typeof(RecordingStrategy)));

        var pipeline = sp.GetRequiredService<ToolInvocationPipeline>();
        var request = new ToolInvocationRequest(
            "DoWrite", "WritePlugin", new Dictionary<string, object?>());

        const string writeProposalJson =
            """{"$type":"write","toolName":"DoWrite","timestamp":"2026-01-01T00:00:00Z","envelope":null}""";

        await pipeline.RunAsync(
            request,
            filters => filters,
            neutral => { neutral.Result = writeProposalJson; return Task.CompletedTask; });

        Assert.Equal(["merge", "review"], events);
        Assert.Single(docketStore.Filed);
    }

    // ── Recording fakes ────────────────────────────────────────────────────────

    private sealed class RecordingStrategy(List<string> events) : ITaskInferenceStrategy
    {
        private bool _recorded;

        public string EntityName => "SpyEntity";

        public IReadOnlyList<TaskInferenceField> Fields
        {
            get
            {
                if (!_recorded)
                {
                    _recorded = true;
                    events.Add("merge");
                }

                return [];
            }
        }

        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class RecordingReviewContextProvider(List<string> events, ReviewContext context)
        : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal)
        {
            events.Add("review");
            return context;
        }
    }

    private static ReviewContext BuildReviewContext() => new(
        SessionId: "session-test",
        TenantId: "tenant-test",
        UserId: "user-test",
        ReviewerUserId: "reviewer-test",
        Affidavit: new Affidavit(
            OperationType: "create",
            EntityType: "SpyEntity",
            EntityId: null,
            Fields: [],
            AggregateConfidence: 1.0f,
            Warnings: [],
            RequiresConfirmation: false));

    private sealed class StandingOrderPolicy : IApprovalPolicy
    {
        public Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
            => Task.FromResult<ReviewRequirement?>(ReviewRequirement.StandingOrder);
    }

    private sealed class UnusedStreamingTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new InvalidOperationException("SendAsync should not be called");

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new InvalidOperationException("BroadcastToGroupAsync should not be called");

        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException("AwaitEvidenceCardResponseAsync should not be called");
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

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
            => Task.CompletedTask;
    }
}
