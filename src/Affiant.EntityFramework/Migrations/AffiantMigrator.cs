using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Affiant.EntityFramework.Migrations;

public static class AffiantMigrator
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>
    /// Idempotently applies the Affiant schema to the configured database.
    /// On Postgres: runs pending EF migrations (migration history preserved).
    /// On SQLite: calls EnsureCreatedAsync, then heals known drift — see
    /// <see cref="HealSqliteDriftAsync"/> remarks for why SQLite cannot simply run the checked-in
    /// migrations the way Postgres does.
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
                await HealSqliteDriftAsync(context, logger, cancellationToken);
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

    /// <summary>
    /// <c>EnsureCreatedAsync</c> only builds a schema that does not exist yet — it no-ops for any
    /// database file that already exists, regardless of whether its schema matches the current
    /// model (affiant#33). That leaves every already-provisioned SQLite host (e.g. HR) permanently
    /// on whatever schema it had at first startup, silently missing any column added since.
    /// </summary>
    /// <remarks>
    /// The checked-in <c>Migrations/</c> classes cannot close this the way they do for Postgres:
    /// they were generated with the Npgsql provider active, so their baked-in column names/types
    /// follow Npgsql conventions (e.g. <c>DocketEntryEntity.AffidavitJson</c> renamed to
    /// <c>Affidavit</c>, typed <c>jsonb</c>) that diverge from what <see cref="AffiantDbContext"/>'s
    /// SQLite branch actually maps that same property to (column name <c>AffidavitJson</c>, type
    /// <c>TEXT</c> — see <see cref="AffiantDbContext.OnModelCreating"/>'s Npgsql-only
    /// <c>HasColumnName</c> overrides). Running <c>Database.MigrateAsync</c> against SQLite with
    /// this migration set produces a table EF's own SQLite-mapped model cannot query. A genuinely
    /// SQLite-native migration history is a larger change (a second, SQLite-targeted migrations set
    /// — effectively a second DbContext, since EF resolves migrations by the
    /// <c>[DbContext(typeof(...))]</c> attribute they were generated under) than this fix covers;
    /// tracked as a follow-up, not silently deferred.
    /// <para>
    /// So instead of a general reconciliation engine, this heals the one known drift directly and
    /// idempotently: a pre-existing <c>Docket</c> table missing <see cref="Affiant.EntityFramework.Models.DocketEntryEntity.ResubmittedTo"/>
    /// (Area-5 Decision 2, affiant#31) gets the column and its index added with the exact shape
    /// <c>EnsureCreatedAsync</c> would have given a database created fresh under today's model —
    /// verified column-for-column against a real <c>EnsureCreatedAsync</c> schema dump. A future
    /// column addition needs the same treatment here until the SQLite-native migration history
    /// above exists.
    /// </para>
    /// </remarks>
    private static async Task HealSqliteDriftAsync(
        AffiantDbContext context, ILogger? logger, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await context.Database.OpenConnectionAsync(cancellationToken);

        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('Docket') WHERE name = 'ResubmittedTo'";
        var hasColumn = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(cancellationToken) ?? 0L) > 0;
        if (hasColumn)
            return;

        logger?.LogInformation(
            "SQLite Docket table predates DocketEntry.ResubmittedTo (affiant#31/D2) — adding column.");

        await using var addColumnCommand = connection.CreateCommand();
        addColumnCommand.CommandText = "ALTER TABLE Docket ADD COLUMN ResubmittedTo TEXT NULL";
        await addColumnCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var addIndexCommand = connection.CreateCommand();
        addIndexCommand.CommandText =
            "CREATE INDEX IF NOT EXISTS IX_Docket_ResubmittedTo ON Docket (ResubmittedTo)";
        await addIndexCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
