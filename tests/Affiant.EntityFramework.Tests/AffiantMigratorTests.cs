using Affiant.EntityFramework.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Affiant.EntityFramework.Tests;

/// <summary>
/// Refuter regression (area-5 refuter round): <c>Database.EnsureCreatedAsync</c> no-ops for any
/// SQLite database that already exists, regardless of whether its schema matches the current
/// model — so a pre-existing host (e.g. HR, "SQLite-only everywhere" per the area-5 paper) never
/// picked up <c>DocketEntry.ResubmittedTo</c> (Area-5 Decision 2, affiant#31) just by restarting on
/// a framework version that added the column. <see cref="AffiantMigrator.MigrateAffiantSchemaAsync"/>
/// must heal that drift directly instead of silently leaving it in place.
/// </summary>
public sealed class AffiantMigratorTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private AffiantDbContext BuildContext() =>
        new(new DbContextOptionsBuilder<AffiantDbContext>().UseSqlite(_connection).Options);

    [Fact]
    public async Task MigrateAffiantSchemaAsync_FreshSqliteDatabase_CreatesResubmittedToColumn()
    {
        await using var db = BuildContext();

        await db.MigrateAffiantSchemaAsync();

        Assert.True(await HasResubmittedToColumnAsync());
    }

    /// <summary>
    /// Builds the Docket table exactly as <c>Database.EnsureCreatedAsync</c> shaped it before
    /// <c>DocketEntry.ResubmittedTo</c> existed (verified column-for-column against a real
    /// EnsureCreatedAsync dump of the pre-affiant#31 model) — the true legacy state, since SQLite
    /// hosts never went through EF migration history at all.
    /// </summary>
    private async Task SeedLegacyPreResubmissionSchemaAsync()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE "Docket" (
                "EntryId" TEXT NOT NULL CONSTRAINT "PK_Docket" PRIMARY KEY,
                "SessionId" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "UserId" TEXT NOT NULL,
                "ReviewerUserId" TEXT NULL,
                "OperationType" TEXT NOT NULL,
                "AffidavitJson" TEXT NOT NULL DEFAULT '{}',
                "ProvenanceChainsJson" TEXT NOT NULL DEFAULT '{}',
                "AmendmentsJson" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ExpiresAt" TEXT NOT NULL,
                "Status" TEXT NOT NULL DEFAULT 'Pending'
            );
            INSERT INTO Docket
                (EntryId, SessionId, TenantId, UserId, ReviewerUserId, OperationType,
                 AffidavitJson, ProvenanceChainsJson, AmendmentsJson, CreatedAt, ExpiresAt, Status)
            VALUES
                ('11111111-1111-1111-1111-111111111111', 's1', 't1', 'u1', NULL, 'CreateOrder',
                 '{}', '{}', NULL, '2026-08-01T00:00:00+00:00', '2026-08-01T01:00:00+00:00', 'Expired');
            """;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task MigrateAffiantSchemaAsync_LegacyDatabaseMissingResubmittedTo_AddsColumnWithoutLosingData()
    {
        await SeedLegacyPreResubmissionSchemaAsync();
        Assert.False(await HasResubmittedToColumnAsync());

        await using var db = BuildContext();
        await db.MigrateAffiantSchemaAsync();

        Assert.True(await HasResubmittedToColumnAsync());

        var entry = await db.Docket.AsNoTracking().SingleAsync();
        Assert.Equal("Expired", entry.Status);
        Assert.Equal("CreateOrder", entry.OperationType);
        Assert.Null(entry.ResubmittedTo);
    }

    [Fact]
    public async Task MigrateAffiantSchemaAsync_LegacyDatabase_ConsumeForResubmitShapedQuerySucceeds()
    {
        await SeedLegacyPreResubmissionSchemaAsync();

        await using var db = BuildContext();
        await db.MigrateAffiantSchemaAsync();

        var entryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var newEntryId = Guid.NewGuid();

        // The exact query shape SqliteDocketStore.ConsumeForResubmitAsync issues.
        var rowsAffected = await db.Docket
            .Where(d => d.EntryId == entryId && d.Status == "Expired" && d.ResubmittedTo == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ResubmittedTo, newEntryId));

        Assert.Equal(1, rowsAffected);

        var reread = await db.Docket.AsNoTracking().SingleAsync(d => d.EntryId == entryId);
        Assert.Equal(newEntryId, reread.ResubmittedTo);
    }

    [Fact]
    public async Task MigrateAffiantSchemaAsync_CalledTwiceOnLegacyDatabase_IsIdempotent()
    {
        await SeedLegacyPreResubmissionSchemaAsync();

        await using (var db = BuildContext())
        {
            await db.MigrateAffiantSchemaAsync();
        }

        await using var second = BuildContext();
        var ex = await Record.ExceptionAsync(() => second.MigrateAffiantSchemaAsync());

        Assert.Null(ex);
        Assert.True(await HasResubmittedToColumnAsync());
    }

    private async Task<bool> HasResubmittedToColumnAsync()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('Docket') WHERE name = 'ResubmittedTo'";
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }
}
