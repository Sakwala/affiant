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
    /// model (affiant#33). That leaves every already-provisioned SQLite host permanently on
    /// whatever schema it had at first startup, silently missing any column added since.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The checked-in <c>Migrations/</c> classes cannot close this the way they do for Postgres:
    /// they were generated with the Npgsql provider active, so their baked-in column names and types
    /// follow Npgsql conventions (e.g. <c>DocketEntryEntity.AffidavitJson</c> renamed to
    /// <c>Affidavit</c>, typed <c>jsonb</c>) that diverge from what <see cref="AffiantDbContext"/>'s
    /// SQLite branch maps that same property to (column name <c>AffidavitJson</c>, type <c>TEXT</c>).
    /// Running <c>Database.MigrateAsync</c> against SQLite with this migration set produces a table
    /// EF's own SQLite-mapped model cannot query. A genuinely SQLite-native migration history is a
    /// larger change — effectively a second DbContext, since EF resolves migrations by the
    /// <c>[DbContext(typeof(...))]</c> attribute they were generated under — and is tracked as a
    /// follow-up, not silently deferred.
    /// </para>
    /// <para>
    /// So instead of a general reconciliation engine, this heals the known drift directly and
    /// idempotently: every column the current model has that the existing <c>Docket</c> table lacks
    /// is added with the shape <c>EnsureCreatedAsync</c> would have given a database created fresh
    /// today, and the indexes those columns need are created if absent. Adding a column to the list
    /// below is what a future column addition needs here until the SQLite-native migration history
    /// above exists.
    /// </para>
    /// <para>
    /// The two tick columns are then <b>backfilled</b> from the instants they mirror. They exist
    /// because SQLite has no native <c>DateTimeOffset</c> and its EF provider can translate neither
    /// an inequality nor an <c>ORDER BY</c> over one into SQL, so every bounded read — the paged
    /// listings, the deadline comparison, the retention cut-off — reads the integer instead. Left at
    /// their column default of zero, a pre-existing row would read as filed and due at the beginning
    /// of time: expired the moment the sweep ran, and eligible for retention immediately. The
    /// backfill is a read-modify-write over the rows that need it rather than a single UPDATE
    /// because converting SQLite's ISO-8601 text to .NET ticks in SQL loses both precision and the
    /// offset, and this is a record of who authorised what.
    /// </para>
    /// </remarks>
    private static async Task HealSqliteDriftAsync(
        AffiantDbContext context, ILogger? logger, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await context.Database.OpenConnectionAsync(cancellationToken);

        // Every column the model has beyond the original Docket table, with the SQLite type
        // EnsureCreated would give it. Verified column-for-column against a real EnsureCreated dump.
        (string Name, string Ddl)[] columns =
        [
            ("ResubmittedTo", "TEXT NULL"),
            ("ToolName", "TEXT NULL"),
            ("Channel", "TEXT NULL"),
            ("Requirement", "TEXT NULL"),
            ("Execution", "TEXT NULL"),
            ("ExecutionDetail", "TEXT NULL"),
            ("DecisionJson", "TEXT NULL"),
            ("AttestationJson", "TEXT NULL"),
            ("BlockedJson", "TEXT NULL"),
            ("CompositeRef", "TEXT NULL"),
            ("AmendedAffidavitJson", "TEXT NULL"),
            ("AmendedProvenanceChainsJson", "TEXT NULL"),
            ("PreservedAmendmentsJson", "TEXT NULL"),
            ("Supersedes", "TEXT NULL"),
            ("DecidedAt", "TEXT NULL"),
            ("DecidedAtTicks", "INTEGER NULL"),
            ("ProtocolVersion", $"TEXT NOT NULL DEFAULT '{Abstractions.AffiantProtocol.Version}'"),
            ("CreatedAtTicks", "INTEGER NOT NULL DEFAULT 0"),
            ("ExpiresAtTicks", "INTEGER NOT NULL DEFAULT 0"),
        ];

        var added = new List<string>();
        foreach (var (name, ddl) in columns)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Docket') WHERE name = $name";
            var parameter = check.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = name;
            check.Parameters.Add(parameter);

            var present = Convert.ToInt64(
                await check.ExecuteScalarAsync(cancellationToken) ?? 0L,
                System.Globalization.CultureInfo.InvariantCulture) > 0;
            if (present) continue;

            await using var add = connection.CreateCommand();
            add.CommandText = $"ALTER TABLE Docket ADD COLUMN {name} {ddl}";
            await add.ExecuteNonQueryAsync(cancellationToken);
            added.Add(name);
        }

        if (added.Count > 0)
        {
            logger?.LogInformation(
                "SQLite Docket table predates {Count} column(s) of the current model — added: {Columns}.",
                added.Count, string.Join(", ", added));
        }

        string[] indexes =
        [
            "CREATE INDEX IF NOT EXISTS IX_Docket_ResubmittedTo ON Docket (ResubmittedTo)",
            "CREATE INDEX IF NOT EXISTS IX_Docket_Supersedes ON Docket (Supersedes)",
            "CREATE INDEX IF NOT EXISTS IX_Docket_Status_ExpiresAtTicks ON Docket (Status, ExpiresAtTicks)",
            "CREATE INDEX IF NOT EXISTS IX_Docket_TenantId_Status_CreatedAtTicks ON Docket (TenantId, Status, CreatedAtTicks)",
            "CREATE INDEX IF NOT EXISTS IX_Docket_Status_DecidedAtTicks ON Docket (Status, DecidedAtTicks)",
        ];
        foreach (var ddl in indexes)
        {
            await using var createIndex = connection.CreateCommand();
            createIndex.CommandText = ddl;
            await createIndex.ExecuteNonQueryAsync(cancellationToken);
        }

        await BackfillSqliteTicksAsync(context, logger, cancellationToken);
    }

    /// <summary>
    /// Gives every pre-existing row the tick values its instants imply. See
    /// <see cref="HealSqliteDriftAsync"/>'s remarks for why a row left at zero would read as due and
    /// disposable.
    /// </summary>
    private static async Task BackfillSqliteTicksAsync(
        AffiantDbContext context, ILogger? logger, CancellationToken cancellationToken)
    {
        var stale = await context.Docket
            .Where(d => d.CreatedAtTicks == 0 || d.ExpiresAtTicks == 0)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0) return;

        foreach (var row in stale)
        {
            row.CreatedAtTicks = row.CreatedAt.UtcTicks;
            row.ExpiresAtTicks = row.ExpiresAt.UtcTicks;
            row.DecidedAtTicks ??= row.DecidedAt?.UtcTicks;
        }

        await context.SaveChangesAsync(cancellationToken);
        logger?.LogInformation(
            "Backfilled the sortable instants on {Count} pre-existing SQLite Docket row(s).", stale.Count);
    }
}
