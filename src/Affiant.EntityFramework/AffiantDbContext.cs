namespace Affiant.EntityFramework;

using Microsoft.EntityFrameworkCore;

public class AffiantDbContext(DbContextOptions<AffiantDbContext> options, string schemaName = "affiant")
    : DbContext(options)
{
    private readonly string _schemaName = schemaName;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Discovers IEntityTypeConfiguration<T> implementations in this assembly.
        // Affiant.Docket ships its own configurations that are registered here
        // when the host calls ApplyConfigurationsFromAssembly for the Docket assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AffiantDbContext).Assembly);

        // Apply the injected schema name to all entity types so that multi-host
        // shared-database deployments can coexist in one Postgres database
        // (e.g., "affiant_meridian" vs "affiant_hrportal").
        // TODO (Story 8.3): Move schema ownership into each IEntityTypeConfiguration<T>
        // so configurations call ToTable(name, schemaName) with the injected name.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            entityType.SetSchema(_schemaName);
        }
    }
}
