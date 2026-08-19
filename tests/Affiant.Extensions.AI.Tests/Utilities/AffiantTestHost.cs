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
    public static ServiceProvider Build(
        IChatClient chatClient,
        FakeDocketStore docketStore,
        object toolInstance,
        Action<IServiceCollection>? configure = null,
        Action<ExtensionsAIOptions>? configureAdapter = null)
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
        services.AddSingleton(chatClient);

        services.AddScoped<ReviewGate>();
        services.AddSingleton<IStreamingTransport>(new UnusedStreamingTransport());
        services.AddSingleton<IDocketStore>(docketStore);
        services.AddSingleton<IApprovalPolicy>(new StandingOrderPolicy());
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();
        services.AddSingleton<IReviewContextProvider>(new DelegatingReviewContextProvider(
            _ => BuildReviewContext()));

        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The neutral <c>ReviewGateFilter</c> deserializes <c>WriteProposal.Envelope</c> as a plain
    /// object via System.Text.Json, which yields a <c>JsonElement</c> rather than the original CLR
    /// <see cref="Affidavit"/> — so, matching the SK and MAF fixtures, this provider supplies a
    /// constant <see cref="ReviewContext"/> instead of attempting to cast the round-tripped envelope.
    /// </summary>
    public static ReviewContext BuildReviewContext() => new(
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

    public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

    public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
        => Task.CompletedTask;
}
