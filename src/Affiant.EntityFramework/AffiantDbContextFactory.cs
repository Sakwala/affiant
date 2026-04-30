using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Affiant.EntityFramework;

/// <summary>
/// Design-time factory for <see cref="AffiantDbContext"/>. Used exclusively by
/// <c>dotnet ef</c> tooling — never invoked at runtime.
/// Required because <c>AddAffiantEntityFramework()</c> registers the context only
/// when called by a host, so the design-time loader cannot discover it otherwise.
/// </summary>
public sealed class AffiantDbContextFactory : IDesignTimeDbContextFactory<AffiantDbContext>
{
    public AffiantDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("AFFIANT_EF_DESIGN_TIME_CONNECTION")
            ?? "Host=localhost;Database=affiant_design_time;Username=postgres;Password=placeholder";
        var optionsBuilder = new DbContextOptionsBuilder<AffiantDbContext>();
        optionsBuilder.UseNpgsql(conn);
        return new AffiantDbContext(optionsBuilder.Options);
    }
}
