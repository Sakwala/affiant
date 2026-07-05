using Affiant.Abstractions.Models;
using Affiant.EntityFramework.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Affiant.EntityFramework.Tests;

/// <summary>
/// Validates the Phase 1 R2 invariant: an <see cref="AffiantChatMessage"/> carrying tool-call
/// fields (tool-call id, function name, serialized arguments) round-trips without data loss
/// through the SQLite store (in-memory). Postgres round-trip is covered separately by the host
/// integration suite.
/// </summary>
public sealed class ChatSessionRoundTripTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private AffiantDbContext _db = null!;
    private SqliteChatSessionStore _store = null!;

    public async Task InitializeAsync()
    {
        // Keep the connection open for the lifetime of the test so the in-memory
        // database survives across multiple EF commands (each command opens a
        // connection; if the connection closes the DB is destroyed).
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AffiantDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AffiantDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _store = new SqliteChatSessionStore(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task RoundTrips_ToolCallFields()
    {
        var session = await _store.CreateAsync("t1", "u1", default);

        var msg = new AffiantChatMessage("assistant", string.Empty)
        {
            ToolCallId = "call_001",
            FunctionName = "SearchThing",
            ArgumentsJson = """{"id":"X-123"}""",
        };

        await _store.SaveMessagesAsync(session.SessionId, [msg], default);
        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);

        var only = Assert.Single(loaded);
        Assert.Equal("assistant", only.Role);
        Assert.Equal("call_001", only.ToolCallId);
        Assert.Equal("SearchThing", only.FunctionName);
        Assert.Equal("""{"id":"X-123"}""", only.ArgumentsJson);
    }

    [Fact]
    public async Task RoundTrips_ToolResultFields()
    {
        var session = await _store.CreateAsync("t1", "u1", default);

        var msg = new AffiantChatMessage("tool", "Found it.")
        {
            ToolCallId = "call_001",
            FunctionName = "SearchThing",
        };

        await _store.SaveMessagesAsync(session.SessionId, [msg], default);
        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);

        var only = Assert.Single(loaded);
        Assert.Equal("tool", only.Role);
        Assert.Equal("Found it.", only.Content);
        Assert.Equal("call_001", only.ToolCallId);
        Assert.Equal("SearchThing", only.FunctionName);
    }

    [Fact]
    public async Task SaveMessages_ReplaceAll_DropsOldMessages()
    {
        var session = await _store.CreateAsync("t1", "u1", default);

        await _store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "hello"),
            new AffiantChatMessage("assistant", "world"),
        ], default);

        await _store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("system", "new"),
        ], default);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);
        Assert.Single(loaded);
        Assert.Equal("new", loaded[0].Content);
    }
}
