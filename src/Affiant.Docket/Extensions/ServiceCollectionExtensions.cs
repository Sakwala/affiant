using Affiant.Abstractions.Interfaces;
using Affiant.Core.Extensions;
using Affiant.Docket.Options;
using Affiant.Docket.Services;
using Affiant.Docket.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Affiant.Docket.Extensions;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> registering the Affiant Docket —
/// the review queue's backend-neutral half.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DocketExpiryService"/> (always) and, when
    /// <see cref="DocketOptions.UseInMemory"/> is selected, <see cref="InMemoryDocketStore"/> as the
    /// <c>IDocketStore</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Optional store selection. Omit it (or select nothing) when the <c>IDocketStore</c> comes from
    /// another package — which is the normal arrangement for a SQL-backed host, see the remarks.
    /// </param>
    /// <returns>The <paramref name="services"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <b>Which call registers the store (changed by affiant#35, area-8 ruling 1, 2026-08-20).</b>
    /// This package implements exactly one <c>IDocketStore</c>: the process-local
    /// <see cref="InMemoryDocketStore"/>. The SQLite- and PostgreSQL-backed stores moved into
    /// <c>Affiant.EntityFramework</c> (they take its <c>AffiantDbContext</c>, and its package already
    /// owned the Docket entity, configuration and migrations) and are registered by
    /// <c>AddAffiantEntityFramework(ef =&gt; ef.UseSqlite(...) / ef.UsePostgres(...))</c>. See
    /// <see cref="DocketOptions"/>'s remarks for why.
    /// </para>
    /// <list type="bullet">
    /// <item>In-memory host: <c>AddAffiantDocket(d =&gt; d.UseInMemory())</c> — nothing else needed.</item>
    /// <item>SQL-backed host: <c>AddAffiantEntityFramework(ef =&gt; ef.UseSqlite(cs))</c> registers the
    /// store; <c>AddAffiantDocket()</c> is still required for the expiry sweep below.</item>
    /// </list>
    /// <para>
    /// <b>Why this method no longer throws when no store is selected.</b> It used to demand exactly one
    /// of <c>UsePostgres</c>/<c>UseSqlite</c>/<c>UseInMemory</c> at registration time. With the SQL
    /// selections gone, "no selection" is a legitimate, common call shape (the SQL-backed host above),
    /// so a registration-time throw would fire on correct wiring. The loudness that guard provided is
    /// not lost — it moved to a check that is actually able to see the whole composition root:
    /// <c>AddAffiantCore</c> registers a startup validator (area-8 ruling 6) that fails the host at
    /// startup, naming the missing registration and the package that provides it, if <em>no</em>
    /// package registered an <c>IDocketStore</c> (or an <c>IStreamingTransport</c>) by the time the
    /// application starts. Registration order between <c>AddAffiantDocket</c>,
    /// <c>AddAffiantEntityFramework</c> and <c>AddAffiantCore</c> therefore does not matter.
    /// </para>
    /// <para>
    /// <see cref="DocketExpiryService"/> is registered unconditionally: it is backend-neutral (it
    /// resolves <c>IDocketStore</c> per tick from a fresh scope) and it is what guarantees lapsed-TTL
    /// entries reach <c>Expired</c> and still-<c>Pending</c> Evidence Cards are re-broadcast.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAffiantDocket(
        this IServiceCollection services,
        Action<DocketOptions>? configure = null)
    {
        var options = new DocketOptions();
        configure?.Invoke(options);

        // DocketExpiryService reads AffiantCoreOptions.DocketExpiryWarningWindow. TryAdd so a
        // host's own AddAffiantCore() registration (real TTL/warning-window config) always wins;
        // this default only fills the gap for hosts that use Affiant.Docket without Affiant.Core.
        services.TryAddSingleton(new AffiantCoreOptions());

        if (options.UseInMemoryProvider)
        {
            services.AddSingleton<IDocketStore, InMemoryDocketStore>();
        }

        services.AddHostedService<DocketExpiryService>();

        return services;
    }
}
