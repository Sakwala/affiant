using Affiant.Abstractions.Models;
using Affiant.EntityFramework.Stores;
using Affiant.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Affiant.EntityFramework.Tests;

/// <summary>
/// Area-5 P2a (affiant#27), Postgres side: mirrors <see cref="SqliteChatSessionStoreAppendTests"/>
/// against a real Postgres instance via <see cref="PostgresFixture"/> (Testcontainers) — the
/// framework's own coverage gap the store-parity pack flagged (<c>PostgresChatSessionStore</c> had
/// zero behavioral tests inside the framework repo before this).
/// </summary>
[Collection("Postgres")]
public sealed class PostgresChatSessionStoreAppendTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AffiantDbContext _db = null!;
    private PostgresChatSessionStore _store = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AffiantDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        _db = new AffiantDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _store = new PostgresChatSessionStore(_db);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task AppendMessagesAsync_ToEmptySession_PersistsMessagesInOrder()
    {
        var session = await _store.CreateAsync("t1", "u1", default);

        await _store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "hello"),
            new AffiantChatMessage("assistant", "world"),
        ], default);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);
        Assert.Equal(["hello", "world"], loaded.Select(m => m.Content));
    }

    [Fact]
    public async Task AppendMessagesAsync_AfterExistingMessages_ContinuesOrdinalFromMaxPlusOne()
    {
        var session = await _store.CreateAsync("t1", "u1", default);
        await _store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("system", "prompt"),
            new AffiantChatMessage("user", "first"),
        ], default);

        await _store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("assistant", "second"),
            new AffiantChatMessage("user", "third"),
        ], default);

        var ordinals = await _db.ChatMessages
            .Where(m => m.SessionId == session.SessionId)
            .OrderBy(m => m.Ordinal)
            .Select(m => m.Ordinal)
            .ToListAsync();
        Assert.Equal([0, 1, 2, 3], ordinals);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);
        Assert.Equal(["prompt", "first", "second", "third"], loaded.Select(m => m.Content));
    }

    [Fact]
    public async Task AppendMessagesAsync_MultipleCallsContinueOrdinalSequentially()
    {
        var session = await _store.CreateAsync("t1", "u1", default);

        await _store.AppendMessagesAsync(session.SessionId, [new AffiantChatMessage("user", "a")], default);
        await _store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("assistant", "b"),
            new AffiantChatMessage("user", "c"),
        ], default);
        await _store.AppendMessagesAsync(session.SessionId, [new AffiantChatMessage("assistant", "d")], default);

        var ordinals = await _db.ChatMessages
            .Where(m => m.SessionId == session.SessionId)
            .OrderBy(m => m.Ordinal)
            .Select(m => m.Ordinal)
            .ToListAsync();
        Assert.Equal([0, 1, 2, 3], ordinals);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);
        Assert.Equal(["a", "b", "c", "d"], loaded.Select(m => m.Content));
    }

    [Fact]
    public async Task AppendMessagesAsync_DoesNotTouchAlreadyDurableRows()
    {
        var session = await _store.CreateAsync("t1", "u1", default);
        await _store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "original"),
        ], default);

        var beforeMessageId = await _db.ChatMessages
            .Where(m => m.SessionId == session.SessionId)
            .Select(m => m.MessageId)
            .SingleAsync();

        await _store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("assistant", "reply"),
        ], default);

        var afterMessageIds = await _db.ChatMessages
            .Where(m => m.SessionId == session.SessionId)
            .Select(m => m.MessageId)
            .ToListAsync();

        Assert.Contains(beforeMessageId, afterMessageIds);
        Assert.Equal(2, afterMessageIds.Count);
    }

    [Fact]
    public async Task AppendMessagesAsync_WithEmptyList_IsNoOp()
    {
        var session = await _store.CreateAsync("t1", "u1", default);
        await _store.SaveMessagesAsync(session.SessionId, [new AffiantChatMessage("user", "only")], default);

        await _store.AppendMessagesAsync(session.SessionId, [], default);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);
        Assert.Single(loaded);
    }

    [Fact]
    public async Task SaveMessagesAsync_AfterAppend_StillReplacesEverything()
    {
        var session = await _store.CreateAsync("t1", "u1", default);
        await _store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "one"),
            new AffiantChatMessage("assistant", "two"),
        ], default);

        await _store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("system", "fresh start"),
        ], default);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, default);
        var only = Assert.Single(loaded);
        Assert.Equal("fresh start", only.Content);
    }
}
