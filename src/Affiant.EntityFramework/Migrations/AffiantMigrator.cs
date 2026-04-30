using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Affiant.EntityFramework.Migrations;

public static class AffiantMigrator
{
    /// <summary>
    /// Idempotently applies all Affiant framework migrations.
    /// Safe to call on every startup — pending migrations are applied exactly once.
    /// </summary>
    public static async Task MigrateAffiantSchemaAsync(
        this AffiantDbContext context,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger?.LogInformation("Applying Affiant schema migrations...");
            await context.Database.MigrateAsync(cancellationToken);
            logger?.LogInformation("Affiant schema migrations completed successfully.");
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
