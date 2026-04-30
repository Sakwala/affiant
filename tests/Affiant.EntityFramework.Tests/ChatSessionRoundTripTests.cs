using System.Text.Json;
using Affiant.EntityFramework.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace Affiant.EntityFramework.Tests;

/// <summary>
/// Validates the Phase 1 R2 invariant: ChatMessageContent carrying
/// FunctionCallContent / FunctionResultContent round-trips without data loss
/// through the SQLite store (in-memory).  Postgres round-trip is covered by
/// Meridian.Api.Tests/Persistence/PostgresChatSessionStoreTests.
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

        _store = new SqliteChatSessionStore(_db, NullLogger<SqliteChatSessionStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task RoundTrips_FunctionCallContent()
    {
        var session = await _store.CreateAsync("t1", "u1", default);

        var args = new KernelArguments { ["tailNumber"] = "9V-SWN" };
        var msg = new ChatMessageContent(AuthorRole.Assistant, string.Empty);
        msg.Items.Add(new FunctionCallContent("SearchAircraft", "FleetPlugin", "call_001", args));

        await _store.SaveMessagesAsync(session.SessionId, [msg], default);
        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);

        Assert.Single(loaded);
        var call = loaded[0].Items.OfType<FunctionCallContent>().FirstOrDefault();
        Assert.NotNull(call);
        Assert.Equal("call_001", call.Id);
        Assert.Equal("SearchAircraft", call.FunctionName);
        Assert.NotNull(call.Arguments);
        // String arguments survive the JSON round-trip (values come back as JsonElement)
        var arg = call.Arguments["tailNumber"];
        var argStr = arg is JsonElement je ? je.GetString() : arg?.ToString();
        Assert.Equal("9V-SWN", argStr);
    }

    [Fact]
    public async Task RoundTrips_FunctionResultContent()
    {
        var session = await _store.CreateAsync("t1", "u1", default);

        var msg = new ChatMessageContent(AuthorRole.Tool, "Found 9V-SWN.");
        msg.Items.Add(new FunctionResultContent("SearchAircraft", "FleetPlugin", "call_001",
            result: "Found 9V-SWN."));

        await _store.SaveMessagesAsync(session.SessionId, [msg], default);
        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);

        Assert.Single(loaded);
        var result = loaded[0].Items.OfType<FunctionResultContent>().FirstOrDefault();
        Assert.NotNull(result);
        Assert.Equal("call_001", result.CallId);
        Assert.Equal("SearchAircraft", result.FunctionName);
        Assert.Equal("Found 9V-SWN.", result.Result?.ToString());
    }

    [Fact]
    public async Task SaveMessages_ReplaceAll_DropsOldMessages()
    {
        var session = await _store.CreateAsync("t1", "u1", default);

        await _store.SaveMessagesAsync(session.SessionId, [
            new ChatMessageContent(AuthorRole.User, "hello"),
            new ChatMessageContent(AuthorRole.Assistant, "world"),
        ], default);

        await _store.SaveMessagesAsync(session.SessionId, [
            new ChatMessageContent(AuthorRole.System, "new"),
        ], default);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);
        Assert.Single(loaded);
        Assert.Equal("new", loaded[0].Content);
    }
}
