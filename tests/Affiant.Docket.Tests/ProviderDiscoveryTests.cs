using Affiant.Abstractions.Interfaces;
using Affiant.Docket.Extensions;
using Affiant.Docket.Services;
using Affiant.Docket.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Affiant.Docket.Tests;

/// <summary>
/// What <c>AddAffiantDocket</c> registers after affiant#35 (area-8 ruling 1, 2026-08-20): the
/// in-memory store when selected, the backend-neutral expiry sweep always, and — deliberately —
/// nothing at all for the SQL backends, which <c>AddAffiantEntityFramework</c> now owns (see
/// <c>Affiant.EntityFramework.Tests.ServiceCollectionExtensionsTests</c> for their coverage).
/// </summary>
public sealed class ProviderDiscoveryTests
{
    [Fact]
    public void AddAffiantDocket_WithInMemory_RegistersInMemoryStore()
    {
        var services = new ServiceCollection();
        services.AddAffiantDocket(options => options.UseInMemory());

        var descriptor = services.FirstOrDefault(sd => sd.ServiceType == typeof(IDocketStore));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(InMemoryDocketStore), descriptor!.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddAffiantDocket_WithNoStoreSelected_RegistersNoDocketStore()
    {
        // The SQL-backed host's call shape: the IDocketStore comes from AddAffiantEntityFramework,
        // this call exists for DocketExpiryService. Registering nothing here is correct, and a host
        // that registers no store anywhere is caught by AddAffiantCore's startup wire-up validator
        // (area-8 ruling 6), not by this method.
        var services = new ServiceCollection();
        services.AddAffiantDocket();

        Assert.DoesNotContain(services, sd => sd.ServiceType == typeof(IDocketStore));
    }

    [Fact]
    public void AddAffiantDocket_RegistersDocketExpiryService()
    {
        var services = new ServiceCollection();
        services.AddAffiantDocket(options => options.UseInMemory());

        var hostedServiceDescriptor = services.FirstOrDefault(sd =>
            sd.ServiceType == typeof(IHostedService) &&
            sd.ImplementationType == typeof(DocketExpiryService));

        Assert.NotNull(hostedServiceDescriptor);
    }

    [Fact]
    public void AddAffiantDocket_WithNoStoreSelected_StillRegistersDocketExpiryService()
    {
        var services = new ServiceCollection();
        services.AddAffiantDocket();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IHostedService) &&
            sd.ImplementationType == typeof(DocketExpiryService));
    }

    [Fact]
    public void AddAffiantDocket_InMemory_CanResolveStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantDocket(options => options.UseInMemory());

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDocketStore>();

        Assert.IsType<InMemoryDocketStore>(store);
    }
}
