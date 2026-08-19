using Affiant.Abstractions.Interfaces;
using Affiant.EntityFramework.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Affiant.EntityFramework.Extensions;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> registering Affiant's EF Core
/// persistence adapter.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AffiantDbContext"/> for the selected provider plus that provider's
    /// <see cref="IChatSessionStore"/> and <see cref="IDocketStore"/> implementations.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Provider selection — exactly one of
    /// <c>UsePostgres</c>/<c>UseSqlite</c>/<c>UseInMemory</c>; throws otherwise.</param>
    /// <returns>The <paramref name="services"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <b>This method registers the SQL-backed <see cref="IDocketStore"/> too (affiant#35, area-8
    /// ruling 1, 2026-08-20).</b> <c>SqliteDocketStore</c>/<c>PostgresDocketStore</c> used to live in
    /// <c>Affiant.Docket</c> and be registered by <c>AddAffiantDocket(d =&gt; d.UseSqlite(...))</c>,
    /// which forced that package to reference this one — an adapter-to-adapter <c>ProjectReference</c>
    /// the repo's layering invariant forbids (<c>CLAUDE.md</c>, "Layering invariant"). Both stores take
    /// this package's <see cref="AffiantDbContext"/> and map this package's <c>DocketEntryEntity</c>
    /// against migrations that already lived here, so they now sit and register here — the exact shape
    /// <see cref="IChatSessionStore"/> already had. A SQL-backed host still calls
    /// <c>AddAffiantDocket()</c> (no store selection) for the backend-neutral
    /// <c>DocketExpiryService</c>; it no longer selects a Docket provider there.
    /// </para>
    /// <para>
    /// <b>The <c>UseInMemory</c> branch deliberately registers no <see cref="IDocketStore"/>.</b> This
    /// package has no in-memory Docket implementation — <c>InMemoryDocketStore</c> is
    /// <c>Affiant.Docket</c>'s, and referencing it from here would simply invert the same forbidden
    /// edge. A fully in-memory host calls <c>AddAffiantDocket(d =&gt; d.UseInMemory())</c> for the
    /// Docket half and <c>AddAffiantEntityFramework(ef =&gt; ef.UseInMemory())</c> for the chat-session
    /// half. A host that registers neither is caught at startup by <c>AddAffiantCore</c>'s wire-up
    /// validator (area-8 ruling 6), not at its first write.
    /// </para>
    /// </remarks>
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
            services.AddScoped<IDocketStore, PostgresDocketStore>();
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
            services.AddScoped<IDocketStore, SqliteDocketStore>();
        }
        else if (options.UseInMemoryProvider)
        {
            // No IDocketStore here on purpose — see this method's remarks.
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
