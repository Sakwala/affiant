using Affiant.Abstractions.Interfaces;
using Affiant.Docket.Extensions;
using Affiant.Docket.Services;
using Affiant.Docket.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Affiant.Docket.Tests;

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
    public void AddAffiantDocket_WithPostgres_RegistersPostgresStore()
    {
        var services = new ServiceCollection();
        services.AddAffiantDocket(options => options.UsePostgres("Host=localhost;Database=test"));

        var descriptor = services.FirstOrDefault(sd => sd.ServiceType == typeof(IDocketStore));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(PostgresDocketStore), descriptor!.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddAffiantDocket_WithSqlite_RegistersSqliteStore()
    {
        var services = new ServiceCollection();
        services.AddAffiantDocket(options => options.UseSqlite("Data Source=:memory:"));

        var descriptor = services.FirstOrDefault(sd => sd.ServiceType == typeof(IDocketStore));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(SqliteDocketStore), descriptor!.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddAffiantDocket_WithNoProvider_Throws()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddAffiantDocket(options => { }));

        Assert.Contains("exactly one provider", ex.Message);
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
