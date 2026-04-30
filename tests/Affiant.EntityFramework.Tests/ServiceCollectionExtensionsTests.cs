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
    public void AddAffiantEntityFramework_WithoutProvider_Throws()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddAffiantEntityFramework(_ => { }));

        Assert.Contains("UsePostgres or UseSqlite", ex.Message);
    }
}
