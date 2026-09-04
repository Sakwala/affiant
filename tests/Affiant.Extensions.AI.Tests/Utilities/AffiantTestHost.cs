namespace Affiant.Extensions.AI.Tests.Utilities;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Builds the smallest DI container that exercises the real Affiant stack end-to-end at the
/// Microsoft.Extensions.AI seam: <c>AddAffiantCore</c> + <c>AddAffiantExtensionsAI</c>, a real
/// <see cref="ReviewGate"/> over an in-memory docket, and a standing-order approval policy so a
/// filed proposal resolves synchronously.
///
/// Mirrors <c>Affiant.AgentFramework.Tests.Extensions.WithAffiantIntegrationTests</c>'s fixture set
/// so the two adapters' integration tests are read side by side.
/// </summary>
internal static class AffiantTestHost
{
    /// <param name="approvalPolicy">
    /// The approval policy <c>ApprovalPolicyEvaluator</c> consults. Defaults to a standing order, so
    /// a filed proposal auto-approves and resolves synchronously — the "already decided, nothing to
    /// wait for" half of the review gate. Pass a policy returning
    /// <see cref="ReviewRequirement.ReviewerConfirmation"/> to exercise the other half (a human must
    /// act, so the turn ends and an Evidence Card goes out). It is a constructor parameter rather
    /// than something a <paramref name="configure"/> callback can add, because
    /// <c>ApprovalPolicyEvaluator</c> takes the FIRST registered policy that returns a requirement —
    /// a policy appended later could never win.
    /// </param>
    /// <param name="transport">
    /// The streaming transport <c>ReviewGate</c> broadcasts Evidence Cards on. Defaults to one that
    /// throws on every call, so a test that does not expect any client traffic fails loudly rather
    /// than silently tolerating it.
    /// </param>
    public static ServiceProvider Build(
        IChatClient chatClient,
        FakeDocketStore docketStore,
        object toolInstance,
        Action<IServiceCollection>? configure = null,
        Action<ExtensionsAIOptions>? configureAdapter = null,
        IApprovalPolicy? approvalPolicy = null,
        IStreamingTransport? transport = null,
        IChatClient? inferenceChatClient = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(toolInstance.GetType(), toolInstance);

        // The strategy named by [AffiantWriteTool] must be resolvable. Without it
        // TaskInferenceMergeFilter throws mid-chain, ToolErrorFilter catches it under
        // surface-and-continue, and every post-tool filter OUTSIDE the merge filter — including
        // ReviewGateFilter's docket filing — is silently skipped. Same registration as the MAF
        // fixture's (Affiant.AgentFramework.Tests WithAffiantIntegrationTests.BuildServices).
        services.AddSingleton<WidgetStrategy>();
        services.AddAffiantCore(opts =>
        {
            opts.EnableObservability = false;
            // This fixture registers IStreamingTransport/IDocketStore itself, so the Area-8 startup
            // wire-up validator has nothing to complain about; the acknowledgment flag stays off so
            // a regression that dropped either registration would still surface here.
        });
        services.AddAffiantExtensionsAI(configureAdapter);

        // ExtensionsAIInferenceCompletionPort resolves IChatClient — required because a registered
        // write-intent tool makes InferenceTriggerFilter construct TaskInferenceRunner (and
        // therefore the port) merely by being resolved from the filter enumerable.
        //
        // Defaults to the same client the chat loop runs on, which is fine for tests that only care
        // about what the tool did. Tests that use the loop client's call count as a
        // loop-continuation witness must pass a SEPARATE inferenceChatClient: task inference for a
        // write tool is a real extra completion call, so a shared client's count would silently
        // conflate "the loop went back to the model" with "inference ran".
        services.AddSingleton(inferenceChatClient ?? chatClient);

        services.AddScoped<ReviewGate>();
        services.AddSingleton(transport ?? new UnusedStreamingTransport());
        services.AddSingleton<IDocketStore>(docketStore);
        services.AddSingleton(approvalPolicy ?? new StandingOrderPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();

        // Who may decide is the host's answer (AZ-2), and the framework's default refuses everyone.
        // This host admits everyone: these tests are about the seam, not about authorization.
        services.AddSingleton<IDecisionAuthorizationPolicy, AdmitEveryone>();
        services.AddSingleton<IReviewContextProvider>(new DelegatingReviewContextProvider(
            BuildReviewContext));

        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The review context a proposal is filed under.
    /// </summary>
    /// <remarks>
    /// The turn's own identity is constant — these fixtures run one session in one tenant — but the
    /// <b>Affidavit is the proposal's own</b>. An entry id is derived from the tool and the canonical
    /// form of what it swore (GT-4), so a provider that handed every proposal the same record would
    /// make every filing in a conversation a replay of the first, which is a property of the fixture
    /// and not of the framework.
    /// </remarks>
    /// <param name="proposal">The proposal being filed, or <c>null</c> for the constant record.</param>
    public static ReviewContext BuildReviewContext(WriteProposal? proposal = null)
    {
        var context = ConstantReviewContext();
        return SwornBy(proposal) is { } sworn ? context with { Affidavit = sworn } : context;
    }

    /// <summary>
    /// The Affidavit a proposal swore, read back off the round-tripped envelope.
    /// </summary>
    /// <remarks>
    /// The neutral <c>ReviewGateFilter</c> deserializes <c>WriteProposal.Envelope</c> as a plain
    /// <c>object</c>, so what arrives is a <c>JsonElement</c> and not the original CLR record. A
    /// host re-reads it, which is what this does; a host that could not would build the record from
    /// its own state instead. Either way the record has to be the proposal's own: an entry id is
    /// derived from the tool and the canonical form of what it swore (GT-4), so a provider handing
    /// every proposal the same record would make every filing in a conversation a replay of the
    /// first.
    /// </remarks>
    private static Affidavit? SwornBy(WriteProposal? proposal)
    {
        if (proposal?.Envelope is not { } envelope)
            return null;

        if (envelope is Affidavit already)
            return already;

        try
        {
            var options = Affiant.Abstractions.Serialization.AffiantJson.SerializerOptions;
            var read = System.Text.Json.JsonSerializer.Deserialize<Affidavit>(
                System.Text.Json.JsonSerializer.Serialize(envelope, options), options);

            // Only a record that actually swears to something: a half-read envelope would be filed
            // as a proposal that swears to nothing, which the gate refuses (GT-3), and the fixture
            // would be measuring the round trip rather than its own subject.
            return read is { Fields.Length: > 0 } ? read : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static ReviewContext ConstantReviewContext() => new(
        SessionId: "session-test",
        TenantId: "tenant-test",
        UserId: "user-test",
        ReviewerUserId: "reviewer-test",
        Affidavit: new Affidavit(
            OperationType: "create",
            EntityType: "Widget",
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

    private sealed class DelegatingReviewContextProvider(Func<WriteProposal, ReviewContext?> build)
        : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => build(proposal);
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

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new InvalidOperationException("UnusedStreamingTransport.AwaitEvidenceCardResponseAsync should not be called");
    }
}

/// <summary>
/// Stand-in for the structured-extraction LLM edge, kept separate from the chat loop's own client so
/// a test can use that client's call count as a loop-continuation witness. Always answers an empty
/// JSON object: task inference finds nothing to merge, which is exactly what these tests want — the
/// inference call must happen (it is part of the real write path) without contributing anything the
/// review-gate assertions would have to account for.
/// </summary>
internal sealed class StubInferenceChatClient : IChatClient
{
    public int CallCount { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Structured-output inference never streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// Approval policy demanding a human reviewer, so <c>ReviewGate.FileForReviewAsync</c> takes its
/// <c>RequiresReview</c> branch: file the entry as Pending, broadcast the Evidence Card, and hand
/// <c>ReviewGateFilter</c> the verdict that ends the model's turn. The counterpart of
/// <c>AffiantTestHost</c>'s default standing order.
/// </summary>
internal sealed class ReviewerConfirmationPolicy : IApprovalPolicy
{
    public Task<ApprovalVerdict?> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
        => Task.FromResult<ApprovalVerdict?>(ReviewRequirement.ReviewerConfirmation);
}

/// <summary>
/// The host authorization port for a seam test: every principal may act on every entry. The
/// framework's own default is a deny-all, and these tests exercise the tool seam rather than the
/// question of who is entitled to approve.
/// </summary>
internal sealed class AdmitEveryone : IDecisionAuthorizationPolicy
{
    public Task<bool> MayDecideAsync(
        Principal principal, DocketEntry entry, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}

/// <summary>
/// <see cref="IStreamingTransport"/> that records every broadcast instead of throwing, so a test can
/// assert the Evidence Card actually left the framework. <see cref="IStreamingTransport.TryDeliverResponse"/>
/// is deliberately left at its interface default (<c>false</c>, "no live waiter"): the non-blocking
/// filing path registers no waiter, so a decision arriving later must travel
/// <c>ReviewGate.HandleDecisionAsync</c>'s restart path and land in the docket store — which is the
/// half of the round trip these tests need to observe.
/// </summary>
internal sealed class RecordingStreamingTransport : IStreamingTransport
{
    public readonly List<(string GroupId, TransportEvent Event, object Payload)> Broadcasts = [];

    public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
        => Task.CompletedTask;

    public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
    {
        Broadcasts.Add((groupId, eventType, payload));
        return Task.CompletedTask;
    }

    public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
        string sessionGroupId, Guid docketId, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "RecordingStreamingTransport.AwaitEvidenceCardResponseAsync should not be called — the " +
            "framework's default filing path is non-blocking and never awaits a response inline.");
}

/// <summary>In-memory <see cref="IDocketStore"/> recording everything filed through it.</summary>
internal sealed class FakeDocketStore : IDocketStore
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
        /// The guarded compare-and-set, over this double's own list — the seam test decides a real
        /// entry, so this is the one member here that has to behave rather than refuse.
        /// </summary>
        Task<DocketTransitionResult> IDocketStore.TransitionAsync(
            Guid entryId, DocketScope scope, ReviewStatus expected, DocketTransitionPatch patch, CancellationToken ct)
        {
            var idx = Filed.FindIndex(e => e.EntryId == entryId && DocketRow.InScope(e, scope));
            if (idx < 0)
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.NotFound());

            var now = DateTimeOffset.UtcNow;
            var current = Filed[idx];
            if (DocketRow.ReadStatus(current, now) == ReviewStatus.Expired && patch.Status != ReviewStatus.Expired)
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.Expired());
            if (current.Status != ReviewStatus.Pending)
                return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.AlreadyDecided());

            var updated = DocketRow.Apply(current, patch, now);
            Filed[idx] = updated;
            return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.Transitioned(updated));
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

        Task<int> IDocketStore.MarkBlockedAsync(Guid entryId, DocketScope scope, BlockedMarker marker, CancellationToken ct)
            => Task.FromResult(0);

        Task<DocketPageResult<DocketEntry>> IDocketStore.ListPendingAsync(
            DocketScope scope, DocketPage page, CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            IReadOnlyList<DocketEntry> items = Filed
                .Where(e => DocketRow.InScope(e, scope) && DocketRow.ReadStatus(e, now) == ReviewStatus.Pending)
                .OrderBy(e => e.CreatedAt)
                .ToList();
            return Task.FromResult(new DocketPageResult<DocketEntry>(items, null, false));
        }

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
