using System.Collections;
using Affiant.Abstractions.Interfaces;
using Affiant.Docket.Stores;
using Affiant.EntityFramework;
using Affiant.EntityFramework.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Affiant.Docket.Tests.Fixtures;

/// <summary>
/// xUnit [ClassData] source that yields one (IDocketStore, FakeTimeProvider, providerName) triple
/// per backend — the clock-driven counterpart to <see cref="DocketStoreProviderFactory"/>. Each
/// store is constructed over the fake clock yielded beside it, so a test moves time by hand and the
/// store's expiry reads move with it.
/// </summary>
public sealed class FakeClockDocketStoreProviderFactory : IEnumerable<object[]>
{
    /// <summary>The instant every clock-driven store test starts from — fixed so failures read the same twice.</summary>
    public static readonly DateTimeOffset Origin = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    // Keep connections alive so SQLite in-memory DBs survive across EF operations.
    private readonly List<SqliteConnection> _connections = [];

    public IEnumerator<object[]> GetEnumerator()
    {
        var inMemoryClock = new FakeTimeProvider(Origin);
        yield return [new InMemoryDocketStore(inMemoryClock), inMemoryClock, "InMemory"];

        var sqliteClock = new FakeTimeProvider(Origin);
        yield return [CreateSqliteStore(sqliteClock), sqliteClock, "SQLite"];

        var postgresClock = new FakeTimeProvider(Origin);
        yield return [CreatePostgresStore(postgresClock), postgresClock, "Postgres"];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IDocketStore CreateSqliteStore(TimeProvider clock)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        _connections.Add(connection);

        var options = new DbContextOptionsBuilder<AffiantDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AffiantDbContext(options);
        db.Database.EnsureCreated();

        return new SqliteDocketStore(db, NullLogger<SqliteDocketStore>.Instance, clock);
    }

    private static IDocketStore CreatePostgresStore(TimeProvider clock)
    {
        StaticPostgresContainer.EnsureSchemaCreated();

        var options = new DbContextOptionsBuilder<AffiantDbContext>()
            .UseNpgsql(StaticPostgresContainer.GetConnectionString())
            .Options;

        return new PostgresDocketStore(
            new AffiantDbContext(options),
            NullLogger<PostgresDocketStore>.Instance,
            clock);
    }
}
