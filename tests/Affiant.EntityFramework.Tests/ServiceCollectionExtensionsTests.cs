using Affiant.Abstractions.Interfaces;
using Affiant.EntityFramework.Extensions;
using Affiant.EntityFramework.Stores;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Affiant.EntityFramework.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAffiantEntityFramework_WithPostgres_RegistersPostgresChatSessionStore()
    {
        var services = new ServiceCollection();
        services.AddAffiantEntityFramework(o => o.UsePostgres("Host=localhost;Database=affiant;"));

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IChatSessionStore));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(PostgresChatSessionStore), descriptor.ImplementationType);
    }

    [Fact]
    public void AddAffiantEntityFramework_WithSqlite_RegistersSqliteChatSessionStore()
    {
        var services = new ServiceCollection();
        services.AddAffiantEntityFramework(o => o.UseSqlite("Data Source=:memory:"));

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IChatSessionStore));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(SqliteChatSessionStore), descriptor.ImplementationType);
    }

    [Fact]
    public void AddAffiantEntityFramework_WithInMemory_RegistersInMemoryChatSessionStore()
    {
        var services = new ServiceCollection();
        services.AddAffiantEntityFramework(o => o.UseInMemory());

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IChatSessionStore));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(InMemoryChatSessionStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    // ── IDocketStore: moved here from Affiant.Docket by affiant#35 (area-8 ruling 1, 2026-08-20) ──
    // The two SQL-backed IDocketStore implementations take this package's AffiantDbContext, so they
    // now live and register here alongside IChatSessionStore instead of forcing Affiant.Docket to
    // reference this package. AddAffiantDocket no longer selects a SQL provider at all.

    [Fact]
    public void AddAffiantEntityFramework_WithPostgres_RegistersPostgresDocketStore()
    {
        var services = new ServiceCollection();
        services.AddAffiantEntityFramework(o => o.UsePostgres("Host=localhost;Database=affiant;"));

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IDocketStore));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(PostgresDocketStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddAffiantEntityFramework_WithSqlite_RegistersSqliteDocketStore()
    {
        var services = new ServiceCollection();
        services.AddAffiantEntityFramework(o => o.UseSqlite("Data Source=:memory:"));

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IDocketStore));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(SqliteDocketStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddAffiantEntityFramework_WithInMemory_RegistersNoDocketStore()
    {
        // This package has no in-memory IDocketStore — InMemoryDocketStore belongs to
        // Affiant.Docket, and referencing it from here would re-create the forbidden
        // adapter-to-adapter edge in the other direction. A fully in-memory host calls
        // AddAffiantDocket(d => d.UseInMemory()) for the Docket half.
        var services = new ServiceCollection();
        services.AddAffiantEntityFramework(o => o.UseInMemory());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDocketStore));
    }

    [Fact]
    public void AddAffiantEntityFramework_WithoutProvider_Throws()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddAffiantEntityFramework(_ => { }));

        Assert.Contains("UsePostgres, UseSqlite, or UseInMemory", ex.Message);
    }
}
