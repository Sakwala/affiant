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
/// Area-8 ruling 6: ReviewGate's lazily-resolved dependencies must fail the host at STARTUP, not at
/// the first real write. These tests are the fail-first proof — a host missing the transport (or the
/// docket store) throws from <c>StartAsync</c>; a fully-wired host starts clean.
/// </summary>
public sealed class AffiantWireUpValidatorTests
{
    [Fact]
    public async Task FullyWiredHost_StartsCleanly()
    {
        var validator = BuildValidator(services =>
        {
            services.AddSingleton<IStreamingTransport, FakeStreamingTransport>();
            services.AddSingleton<IDocketStore, FakeDocketStore>();
        });

        await validator.StartAsync(CancellationToken.None); // must not throw
    }

    [Fact]
    public async Task MissingTransport_ThrowsAtStartup_NamingContractAndPackage()
    {
        var validator = BuildValidator(services =>
            services.AddSingleton<IDocketStore, FakeDocketStore>());

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains(typeof(IStreamingTransport).FullName!, ex.Message);
        Assert.Contains("AddAffiantSignalR", ex.Message);
        Assert.Contains("Affiant.Transport.SignalR", ex.Message);
        // The store IS registered — it must not be reported as missing.
        Assert.DoesNotContain(typeof(IDocketStore).FullName!, ex.Message);
    }

    [Fact]
    public async Task MissingDocketStore_ThrowsAtStartup_NamingBothWaysToSupplyIt()
    {
        var validator = BuildValidator(services =>
            services.AddSingleton<IStreamingTransport, FakeStreamingTransport>());

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains(typeof(IDocketStore).FullName!, ex.Message);
        Assert.Contains("AddAffiantEntityFramework", ex.Message);
        Assert.Contains("AddAffiantDocket", ex.Message);
        Assert.DoesNotContain(typeof(IStreamingTransport).FullName!, ex.Message);
    }

    [Fact]
    public async Task NeitherRegistered_ThrowsNamingBoth()
    {
        var validator = BuildValidator();

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains(typeof(IStreamingTransport).FullName!, ex.Message);
        Assert.Contains(typeof(IDocketStore).FullName!, ex.Message);
        Assert.Contains("AcknowledgeMissingReviewWiring", ex.Message);
    }

    [Fact]
    public async Task Acknowledged_DowngradesToWarning()
    {
        var validator = BuildValidator(
            configureCore: options => options.AcknowledgeMissingReviewWiring = true);

        await validator.StartAsync(CancellationToken.None); // must not throw
    }

    [Fact]
    public void AddAffiantCore_RegistersTheValidatorAsTheFirstHostedService()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();

        var first = services.First(d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(typeof(AffiantWireUpValidator), first.ImplementationType);
    }

    [Fact]
    public void AddAffiantCore_CalledTwice_RegistersTheValidatorOnce()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddAffiantCore();

        Assert.Single(services, d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(AffiantWireUpValidator));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AffiantWireUpValidator BuildValidator(
        Action<IServiceCollection>? wiring = null,
        Action<AffiantCoreOptions>? configureCore = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(configureCore);
        wiring?.Invoke(services);

        var provider = services.BuildServiceProvider();
        return provider.GetServices<IHostedService>().OfType<AffiantWireUpValidator>().Single();
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class FakeStreamingTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeDocketStore : IDocketStore
    {
        public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
            => Task.CompletedTask;

        public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<ConversationContext?>(null);

        public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct) => Task.CompletedTask;

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

        public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

        public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct) => Task.CompletedTask;
    }
}
