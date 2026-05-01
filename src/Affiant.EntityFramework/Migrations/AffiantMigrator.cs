using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Affiant.EntityFramework.Migrations;

public static class AffiantMigrator
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>
    /// Idempotently applies the Affiant schema to the configured database.
    /// On Postgres: runs pending EF migrations (migration history preserved).
    /// On SQLite: calls EnsureCreatedAsync (no migration history; for dev/test use).
    /// </summary>
    public static async Task MigrateAffiantSchemaAsync(
        this AffiantDbContext context,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (context.Database.ProviderName == SqliteProvider)
            {
                logger?.LogInformation("Applying Affiant schema (SQLite — EnsureCreated)...");
                await context.Database.EnsureCreatedAsync(cancellationToken);
                logger?.LogInformation("Affiant schema applied successfully.");
            }
            else
            {
                logger?.LogInformation("Applying Affiant schema migrations...");
                await context.Database.MigrateAsync(cancellationToken);
                logger?.LogInformation("Affiant schema migrations completed successfully.");
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error applying Affiant schema migrations.");
            throw;
        }
    }

    public static async Task<IEnumerable<string>> GetPendingMigrationsAsync(this AffiantDbContext context)
    {
        return await context.Database.GetPendingMigrationsAsync();
    }
}
