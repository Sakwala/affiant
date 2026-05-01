using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Docket.Services;
using Affiant.Docket.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Affiant.Docket.Tests.Integration;

/// <summary>
/// Validates DocketExpiryService.ExpireOverdueAsync bulk-update behavior across all three
/// IDocketStore backends. The service uses IServiceScopeFactory to resolve the store,
/// so the test wires up a minimal ServiceProvider with the test store registered as Singleton.
///
/// Key invariants:
///   - Entries past ExpiresAt are transitioned to Expired on the first tick.
///   - Entries not yet past ExpiresAt remain Pending after the first tick.
///   - Running the tick a second time does not corrupt already-Expired entries (idempotent).
/// </summary>
public sealed class DocketExpiryServiceTests
{
    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ExpireOverdueAsync_BulkExpiry_IsIdempotent(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var now = DateTimeOffset.UtcNow;

        var expiredEntry1 = TestDocketEntry.CreateDefault(expiresAt: now.AddSeconds(-5));
        var expiredEntry2 = TestDocketEntry.CreateDefault(expiresAt: now.AddSeconds(-10));
        var notYetExpired = TestDocketEntry.CreateDefault(expiresAt: now.AddMinutes(5));

        await store.FileDocketEntryAsync(expiredEntry1, CancellationToken.None);
        await store.FileDocketEntryAsync(expiredEntry2, CancellationToken.None);
        await store.FileDocketEntryAsync(notYetExpired, CancellationToken.None);

        var expiryService = BuildExpiryService(store);

        // First tick: expired entries are transitioned; not-yet-expired stays Pending
        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        var afterFirst1 = await store.GetDocketEntryAsync(expiredEntry1.EntryId, CancellationToken.None);
        var afterFirst2 = await store.GetDocketEntryAsync(expiredEntry2.EntryId, CancellationToken.None);
        var afterFirstPending = await store.GetDocketEntryAsync(notYetExpired.EntryId, CancellationToken.None);

        Assert.Equal(ReviewStatus.Expired, afterFirst1!.Status);
        Assert.Equal(ReviewStatus.Expired, afterFirst2!.Status);
        Assert.Equal(ReviewStatus.Pending, afterFirstPending!.Status);

        // Second tick: already-Expired entries are silently skipped (WHERE Status = Pending guard)
        await expiryService.ExpireOverdueAsync(CancellationToken.None);

        var afterSecond1 = await store.GetDocketEntryAsync(expiredEntry1.EntryId, CancellationToken.None);
        var afterSecond2 = await store.GetDocketEntryAsync(expiredEntry2.EntryId, CancellationToken.None);

        Assert.Equal(ReviewStatus.Expired, afterSecond1!.Status);
        Assert.Equal(ReviewStatus.Expired, afterSecond2!.Status);

        // ExpiresAt is unchanged — the second tick wrote nothing
        Assert.Equal(afterFirst1.ExpiresAt, afterSecond1.ExpiresAt);
        Assert.Equal(afterFirst2.ExpiresAt, afterSecond2.ExpiresAt);
    }

    private static DocketExpiryService BuildExpiryService(IDocketStore store)
    {
        // Register the test store as Singleton so the service resolves the same
        // instance that test data was written to — the scope factory is built from
        // a minimal ServiceCollection rather than mocked.
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new DocketExpiryService(scopeFactory, NullLogger<DocketExpiryService>.Instance);
    }
}
