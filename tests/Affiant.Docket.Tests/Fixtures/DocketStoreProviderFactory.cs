using System.Collections;
using Affiant.Abstractions.Interfaces;
using Affiant.Docket.Stores;
using Affiant.EntityFramework;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Affiant.Docket.Tests.Fixtures;

/// <summary>
/// xUnit [ClassData] source that yields one (IDocketStore, providerName) tuple
/// per backend: InMemory, SQLite (in-process), and Postgres (Testcontainers).
///
/// Each [Theory] method that uses this factory gets a fresh set of store instances,
/// so tests are isolated from one another. SQLite uses per-factory in-memory databases;
/// Postgres tests share a container but use unique EntryIds for isolation.
/// </summary>
public sealed class DocketStoreProviderFactory : IEnumerable<object[]>
{
    // Keep connections alive so SQLite in-memory DBs survive across EF operations.
    // Each factory instance is scoped to one [Theory] method by xUnit.
    private readonly List<SqliteConnection> _connections = [];

    // xUnit discovers [Theory] methods concurrently; guard Postgres schema creation
    // so only one factory runs EnsureCreated — subsequent factories reuse the tables.
    private static readonly object s_pgSchemaLock = new();
    private static bool s_pgSchemaCreated;

    public IEnumerator<object[]> GetEnumerator()
    {
        yield return [new InMemoryDocketStore(), "InMemory"];
        yield return [CreateSqliteStore(), "SQLite"];
        yield return [CreatePostgresStore(), "Postgres"];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IDocketStore CreateSqliteStore()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        _connections.Add(connection);

        var options = new DbContextOptionsBuilder<AffiantDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AffiantDbContext(options);
        db.Database.EnsureCreated();

        return new SqliteDocketStore(db, NullLogger<SqliteDocketStore>.Instance);
    }

    private static IDocketStore CreatePostgresStore()
    {
        var cs = StaticPostgresContainer.GetConnectionString();

        var options = new DbContextOptionsBuilder<AffiantDbContext>()
            .UseNpgsql(cs)
            .Options;

        lock (s_pgSchemaLock)
        {
            if (!s_pgSchemaCreated)
            {
                using var initDb = new AffiantDbContext(options);
                initDb.Database.EnsureCreated();
                s_pgSchemaCreated = true;
            }
        }

        return new PostgresDocketStore(
            new AffiantDbContext(options),
            NullLogger<PostgresDocketStore>.Instance);
    }
}
