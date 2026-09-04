namespace Affiant.Core.Tests.Validation;

using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// A host whose write tools declare update operations cannot produce a lawful update Affidavit
/// without a way to read what each field replaces — so it fails at startup rather than filing
/// create-shaped records for updates. A create-only host is unaffected: nothing about its wiring
/// changes.
/// </summary>
public sealed class PreviousValueSourceWireUpTests
{
    private sealed class WidgetStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
            [new("Colour", "string", "Colour of the widget")];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class WidgetPreviousValues : IPreviousValueSource
    {
        public Task<IReadOnlyDictionary<string, object?>?> GetPreviousValuesAsync(
            string entityType, string entityId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, object?>?>(new Dictionary<string, object?>());
    }

    [Fact]
    public async Task AnUpdateToolWithNoSource_FailsAtStartup_NamingTheToolAndTheFix()
    {
        var validator = BuildValidator(services =>
            services.AddAffiantTool<WidgetStrategy>("UpdateWidget", Operation.WriteUpdate, "Widget"));

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains(typeof(IPreviousValueSource).FullName!, ex.Message);
        Assert.Contains("AddPreviousValueSource", ex.Message);
        Assert.Contains("UpdateWidget", ex.Message);
    }

    [Fact]
    public async Task AnUpdateToolWithASource_StartsCleanly()
    {
        var validator = BuildValidator(services =>
        {
            services.AddAffiantTool<WidgetStrategy>("UpdateWidget", Operation.WriteUpdate, "Widget");
            services.AddPreviousValueSource<WidgetPreviousValues>();
        });

        await validator.StartAsync(CancellationToken.None); // must not throw
    }

    [Fact]
    public async Task ACreateOnlyHostWithNoSource_IsUnaffected()
    {
        var validator = BuildValidator(services =>
            services.AddAffiantTool<WidgetStrategy>("CreateWidget", Operation.WriteCreate, "Widget"));

        await validator.StartAsync(CancellationToken.None); // must not throw
    }

    [Fact]
    public async Task AHostWithNoWriteToolsAtAll_IsUnaffected()
    {
        var validator = BuildValidator(services => services.AddAffiantReadTool("FindWidget", "Widget"));

        await validator.StartAsync(CancellationToken.None); // must not throw
    }

    [Fact]
    public async Task TheRefusalIsAcknowledgeable_LikeEveryOtherMissingContract()
    {
        var validator = BuildValidator(
            services => services.AddAffiantTool<WidgetStrategy>("UpdateWidget", Operation.WriteUpdate, "Widget"),
            options => options.AcknowledgeMissingReviewWiring = true);

        await validator.StartAsync(CancellationToken.None); // downgraded to a warning
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AffiantWireUpValidator BuildValidator(
        Action<IServiceCollection> wiring,
        Action<AffiantCoreOptions>? configureCore = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(configureCore);
        // The review loop itself is wired, so the only thing under test is the previous-value port.
        services.AddSingleton<IStreamingTransport, UnusedStreamingTransport>();
        services.AddSingleton<IDocketStore, UnusedDocketStore>();
        wiring(services);

        var provider = services.BuildServiceProvider();
        return provider.GetServices<IHostedService>().OfType<AffiantWireUpValidator>().Single();
    }

    private sealed class UnusedStreamingTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new NotSupportedException();

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedDocketStore : IDocketStore
    {
        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task UpdateAmendmentsAsync(
            Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
            => throw new NotSupportedException();

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
