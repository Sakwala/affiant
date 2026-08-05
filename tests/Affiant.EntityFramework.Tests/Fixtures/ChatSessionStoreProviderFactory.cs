using System.Collections;
using Affiant.Abstractions.Interfaces;
using Affiant.EntityFramework.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Affiant.EntityFramework.Tests.Fixtures;

/// <summary>
/// xUnit [ClassData] source that yields one (IChatSessionStore, providerName) tuple per
/// no-external-dependency backend: InMemory and SQLite (in-process) — mirrors
/// <c>Affiant.Docket.Tests.Fixtures.DocketStoreProviderFactory</c>'s [Theory]-driven pattern so it
/// pins <see cref="IChatSessionStore"/> parity the same way <c>DocketStoreProviderFactory</c> pins
/// <c>IDocketStore</c> parity (Area-5 P4 item I — previously zero framework coverage on any chat-
/// store backend).
///
/// Postgres deliberately does NOT run through this [ClassData] factory: [ClassData] enumerates
/// synchronously at test-discovery time, so a Postgres slot here would need its own
/// Testcontainers spin-up independent of this assembly's existing
/// <c>[Collection("Postgres")]</c>/<see cref="Infrastructure.PostgresCollection"/> fixture — a
/// second, uncoordinated container racing the first one for the Docker daemon reproducibly
/// starved a sibling test class's own container start under load (observed:
/// <c>PostgresChatSessionStoreAppendTests</c> failing with
/// <c>RegexMatchTimeoutException</c> inside Testcontainers' own image-name match, not present
/// without the second container). <see cref="SharedChatSessionStoreTestsPostgres"/> covers the
/// same Postgres behavior via the existing shared fixture instead.
/// </summary>
public sealed class ChatSessionStoreProviderFactory : IEnumerable<object[]>
{
    private readonly List<SqliteConnection> _connections = [];

    public IEnumerator<object[]> GetEnumerator()
    {
        yield return [new InMemoryChatSessionStore(), "InMemory"];
        yield return [CreateSqliteStore(), "SQLite"];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IChatSessionStore CreateSqliteStore()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        _connections.Add(connection);

        var options = new DbContextOptionsBuilder<AffiantDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AffiantDbContext(options);
        db.Database.EnsureCreated();

        return new SqliteChatSessionStore(db);
    }
}
