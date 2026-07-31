using Affiant.Abstractions.Interfaces;
using Affiant.Core.Extensions;
using Affiant.Docket.Options;
using Affiant.Docket.Services;
using Affiant.Docket.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Affiant.Docket.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAffiantDocket(
        this IServiceCollection services,
        Action<DocketOptions> configure)
    {
        var options = new DocketOptions();
        configure(options);

        // DocketExpiryService reads AffiantCoreOptions.DocketExpiryWarningWindow. TryAdd so a
        // host's own AddAffiantCore() registration (real TTL/warning-window config) always wins;
        // this default only fills the gap for hosts that use Affiant.Docket without Affiant.Core.
        services.TryAddSingleton(new AffiantCoreOptions());

        if (!options.UsePostgresProvider && !options.UseSqliteProvider && !options.UseInMemoryProvider)
        {
            throw new InvalidOperationException(
                "AddAffiantDocket requires exactly one provider: call UsePostgres, UseSqlite, or UseInMemory.");
        }

        if (options.UsePostgresProvider)
        {
            services.AddScoped<IDocketStore, PostgresDocketStore>();
        }
        else if (options.UseSqliteProvider)
        {
            services.AddScoped<IDocketStore, SqliteDocketStore>();
        }
        else
        {
            services.AddSingleton<IDocketStore, InMemoryDocketStore>();
        }

        services.AddHostedService<DocketExpiryService>();

        return services;
    }
}
