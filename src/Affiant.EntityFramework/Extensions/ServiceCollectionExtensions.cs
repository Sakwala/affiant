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
        else
        {
            throw new InvalidOperationException(
                "EntityFrameworkOptions must specify either UsePostgres or UseSqlite.");
        }

        return services;
    }
}

public sealed class EntityFrameworkOptions
{
    public string? ConnectionString { get; private set; }
    internal bool UsePostgresProvider { get; private set; }
    internal bool UseSqliteProvider { get; private set; }

    public void UsePostgres(string connectionString)
    {
        if (UseSqliteProvider) throw new InvalidOperationException("Cannot use both Postgres and SQLite.");
        ConnectionString = connectionString;
        UsePostgresProvider = true;
    }

    public void UseSqlite(string connectionString)
    {
        if (UsePostgresProvider) throw new InvalidOperationException("Cannot use both Postgres and SQLite.");
        ConnectionString = connectionString;
        UseSqliteProvider = true;
    }
}
