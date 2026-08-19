using System.Collections;
using Affiant.Abstractions.Interfaces;
using Affiant.Docket.Stores;
using Affiant.EntityFramework;
using Affiant.EntityFramework.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Affiant.Docket.Tests.Fixtures;

/// <summary>
/// xUnit [ClassData] source for genuinely-concurrent store-level theories: yields TWO independent
/// <see cref="IDocketStore"/> instances per backend, both backed by the same underlying data, plus
/// the provider name.
///
/// <see cref="DocketStoreProviderFactory"/> cannot serve this shape — EF Core's <c>DbContext</c> is
/// not safe for concurrent operations on one instance (its own <c>ConcurrencyDetector</c> throws),
/// so a genuine <c>Task.WhenAll</c> race needs a distinct <c>DbContext</c> per caller, the same as
/// two separate requests would get from DI's Scoped lifetime in production. SQLite achieves this via
/// a named, shared-cache in-memory database (<c>Mode=Memory;Cache=Shared</c>) so two connections see
/// the same rows; Postgres just opens two pooled connections against the shared Testcontainer.
/// <see cref="InMemoryDocketStore"/> has no such restriction — both slots are the same instance,
/// since its own internal lock already provides genuine thread safety across callers.
/// </summary>
public sealed class DocketStoreConcurrencyProviderFactory : IEnumerable<object[]>
{
    private readonly List<SqliteConnection> _connections = [];

    public IEnumerator<object[]> GetEnumerator()
    {
        var inMemory = new InMemoryDocketStore();
        yield return [inMemory, inMemory, "InMemory"];

        var (sqliteA, sqliteB) = CreateSqliteStorePair();
        yield return [sqliteA, sqliteB, "SQLite"];

        yield return [CreatePostgresStore(), CreatePostgresStore(), "Postgres"];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private (IDocketStore, IDocketStore) CreateSqliteStorePair()
    {
        var connectionString = $"Data Source=resubmit-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        // A Mode=Memory;Cache=Shared database is dropped once its last connection closes — this
        // "keeper" connection has no DocketStore of its own; it just keeps the shared database
        // alive for the lifetime of connectionA/connectionB below.
        var keeper = new SqliteConnection(connectionString);
        keeper.Open();
        _connections.Add(keeper);

        var connectionA = new SqliteConnection(connectionString);
        connectionA.Open();
        _connections.Add(connectionA);
        var dbA = new AffiantDbContext(new DbContextOptionsBuilder<AffiantDbContext>().UseSqlite(connectionA).Options);
        dbA.Database.EnsureCreated();

        var connectionB = new SqliteConnection(connectionString);
        connectionB.Open();
        _connections.Add(connectionB);
        var dbB = new AffiantDbContext(new DbContextOptionsBuilder<AffiantDbContext>().UseSqlite(connectionB).Options);

        return (
            new SqliteDocketStore(dbA, NullLogger<SqliteDocketStore>.Instance),
            new SqliteDocketStore(dbB, NullLogger<SqliteDocketStore>.Instance));
    }

    private static IDocketStore CreatePostgresStore()
    {
        StaticPostgresContainer.EnsureSchemaCreated();

        var options = new DbContextOptionsBuilder<AffiantDbContext>()
            .UseNpgsql(StaticPostgresContainer.GetConnectionString())
            .Options;

        return new PostgresDocketStore(new AffiantDbContext(options), NullLogger<PostgresDocketStore>.Instance);
    }
}
