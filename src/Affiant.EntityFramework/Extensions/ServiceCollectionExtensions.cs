using Affiant.Abstractions.Interfaces;
using Affiant.EntityFramework.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Affiant.EntityFramework.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAffiantEntityFramework(
        this IServiceCollection services,
        Action<EntityFrameworkOptions> configure)
    {
        var options = new EntityFrameworkOptions();
        configure(options);

        if (options.UsePostgresProvider)
        {
            services.AddDbContext<AffiantDbContext>(dbOptions =>
                dbOptions.UseNpgsql(
                    options.ConnectionString,
                    npgOptions => npgOptions.MigrationsAssembly("Affiant.EntityFramework")
                )
            );
            services.AddScoped<IChatSessionStore, PostgresChatSessionStore>();
        }
        else if (options.UseSqliteProvider)
        {
            services.AddDbContext<AffiantDbContext>(dbOptions =>
                dbOptions.UseSqlite(
                    options.ConnectionString,
                    sqliteOptions => sqliteOptions.MigrationsAssembly("Affiant.EntityFramework")
                )
            );
            services.AddScoped<IChatSessionStore, SqliteChatSessionStore>();
        }
        else if (options.UseInMemoryProvider)
        {
            services.AddSingleton<IChatSessionStore, InMemoryChatSessionStore>();
        }
        else
        {
            throw new InvalidOperationException(
                "EntityFrameworkOptions must specify either UsePostgres, UseSqlite, or UseInMemory.");
        }

        return services;
    }
}

public sealed class EntityFrameworkOptions
{
    public string? ConnectionString { get; private set; }
    internal bool UsePostgresProvider { get; private set; }
    internal bool UseSqliteProvider { get; private set; }
    internal bool UseInMemoryProvider { get; private set; }

    public void UsePostgres(string connectionString)
    {
        if (UseSqliteProvider || UseInMemoryProvider) throw new InvalidOperationException("Cannot combine EntityFramework provider options.");
        ConnectionString = connectionString;
        UsePostgresProvider = true;
    }

    public void UseSqlite(string connectionString)
    {
        if (UsePostgresProvider || UseInMemoryProvider) throw new InvalidOperationException("Cannot combine EntityFramework provider options.");
        ConnectionString = connectionString;
        UseSqliteProvider = true;
    }

    public void UseInMemory()
    {
        if (UsePostgresProvider || UseSqliteProvider) throw new InvalidOperationException("Cannot combine EntityFramework provider options.");
        UseInMemoryProvider = true;
    }
}
