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
/// xUnit [ClassData] source pairing an <see cref="IDocketStore"/> with an
/// <see cref="IChatSessionStore"/> backed by the same underlying connection/schema per backend.
///
/// <c>ConversationContextEntity</c> carries a foreign key onto <c>ChatSessionEntity.SessionId</c>
/// on both SQL backends (cascade delete) — SaveContextAsync/LoadContextAsync theories need a real
/// chat session to exist first, exactly as production callers create one via
/// <see cref="IChatSessionStore.CreateAsync"/> before ever touching
/// <see cref="IDocketStore.SaveContextAsync"/> (see <c>SessionLockRegistry</c>'s own audit note on
/// this call ordering). <see cref="DocketStoreProviderFactory"/> cannot serve this directly since it
/// yields only the docket store; InMemory carries no such constraint but is paired the same way for
/// symmetry.
/// </summary>
public sealed class DocketStoreWithChatSessionProviderFactory : IEnumerable<object[]>
{
    private readonly List<SqliteConnection> _connections = [];

    public IEnumerator<object[]> GetEnumerator()
    {
        yield return [new InMemoryDocketStore(), new InMemoryChatSessionStore(), "InMemory"];

        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        _connections.Add(connection);

        var sqliteOptions = new DbContextOptionsBuilder<AffiantDbContext>().UseSqlite(connection).Options;
        var sqliteDb = new AffiantDbContext(sqliteOptions);
        sqliteDb.Database.EnsureCreated();
        yield return [
            new SqliteDocketStore(sqliteDb, NullLogger<SqliteDocketStore>.Instance),
            new SqliteChatSessionStore(sqliteDb),
            "SQLite"];

        StaticPostgresContainer.EnsureSchemaCreated();
        var postgresOptions = new DbContextOptionsBuilder<AffiantDbContext>()
            .UseNpgsql(StaticPostgresContainer.GetConnectionString())
            .Options;
        var postgresDb = new AffiantDbContext(postgresOptions);
        yield return [
            new PostgresDocketStore(postgresDb, NullLogger<PostgresDocketStore>.Instance),
            new PostgresChatSessionStore(postgresDb),
            "Postgres"];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
