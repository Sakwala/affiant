using Affiant.Abstractions.Models;
using Affiant.EntityFramework.Stores;
using Xunit;

namespace Affiant.EntityFramework.Tests;

/// <summary>
/// Area-5 P2b: direct behavior coverage for <see cref="InMemoryChatSessionStore"/> — mirrors
/// <see cref="SqliteChatSessionStoreAppendTests"/>/<see cref="PostgresChatSessionStoreAppendTests"/>
/// so the same append/full-replace semantics are pinned on the in-memory backend too. A shared
/// [Theory]-driven parity factory across all three <see cref="Affiant.Abstractions.Interfaces.IChatSessionStore"/>
/// backends is P4 (item I); these are direct, per-store tests in the meantime.
/// </summary>
public sealed class InMemoryChatSessionStoreTests
{
    private static InMemoryChatSessionStore CreateStore() => new();

    [Fact]
    public async Task CreateAsync_ThenGetAsync_RoundTripsSession()
    {
        var store = CreateStore();

        var created = await store.CreateAsync("t1", "u1", default);
        var loaded = await store.GetAsync(created.SessionId, default);

        Assert.NotNull(loaded);
        Assert.Equal(created.SessionId, loaded!.SessionId);
        Assert.Equal("t1", loaded.TenantId);
        Assert.Equal("u1", loaded.UserId);
    }

    [Fact]
    public async Task GetAsync_ForMissingSession_ReturnsNull()
    {
        var store = CreateStore();
        Assert.Null(await store.GetAsync("does-not-exist", default));
    }

    [Fact]
    public async Task AppendMessagesAsync_ToEmptySession_PersistsMessagesInOrder()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("t1", "u1", default);

        await store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "hello"),
            new AffiantChatMessage("assistant", "world"),
        ], default);

        var loaded = await store.LoadMessagesAsync(session.SessionId, default);
        Assert.Equal(["hello", "world"], loaded.Select(m => m.Content));
    }

    [Fact]
    public async Task AppendMessagesAsync_AfterExistingMessages_AppendsWithoutTouchingExisting()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("t1", "u1", default);
        await store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("system", "prompt"),
            new AffiantChatMessage("user", "first"),
        ], default);

        await store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("assistant", "second"),
            new AffiantChatMessage("user", "third"),
        ], default);

        var loaded = await store.LoadMessagesAsync(session.SessionId, default);
        Assert.Equal(["prompt", "first", "second", "third"], loaded.Select(m => m.Content));
    }

    [Fact]
    public async Task AppendMessagesAsync_MultipleCallsContinueSequentially()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("t1", "u1", default);

        await store.AppendMessagesAsync(session.SessionId, [new AffiantChatMessage("user", "a")], default);
        await store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("assistant", "b"),
            new AffiantChatMessage("user", "c"),
        ], default);
        await store.AppendMessagesAsync(session.SessionId, [new AffiantChatMessage("assistant", "d")], default);

        var loaded = await store.LoadMessagesAsync(session.SessionId, default);
        Assert.Equal(["a", "b", "c", "d"], loaded.Select(m => m.Content));
    }

    [Fact]
    public async Task AppendMessagesAsync_WithEmptyList_IsNoOp()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("t1", "u1", default);
        await store.SaveMessagesAsync(session.SessionId, [new AffiantChatMessage("user", "only")], default);

        await store.AppendMessagesAsync(session.SessionId, [], default);

        var loaded = await store.LoadMessagesAsync(session.SessionId, default);
        Assert.Single(loaded);
    }

    [Fact]
    public async Task AppendMessagesAsync_ToSessionWithNoPriorSaveMessagesCall_StartsFresh()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("t1", "u1", default);

        await store.AppendMessagesAsync(session.SessionId, [new AffiantChatMessage("user", "first ever")], default);

        var loaded = await store.LoadMessagesAsync(session.SessionId, default);
        var only = Assert.Single(loaded);
        Assert.Equal("first ever", only.Content);
    }

    [Fact]
    public async Task SaveMessagesAsync_AfterAppend_StillReplacesEverything()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("t1", "u1", default);
        await store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "one"),
            new AffiantChatMessage("assistant", "two"),
        ], default);

        await store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("system", "fresh start"),
        ], default);

        var loaded = await store.LoadMessagesAsync(session.SessionId, default);
        var only = Assert.Single(loaded);
        Assert.Equal("fresh start", only.Content);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSessionAndMessages()
    {
        var store = CreateStore();
        var session = await store.CreateAsync("t1", "u1", default);
        await store.SaveMessagesAsync(session.SessionId, [new AffiantChatMessage("user", "hi")], default);

        await store.DeleteAsync(session.SessionId, default);

        Assert.Null(await store.GetAsync(session.SessionId, default));
        Assert.Empty(await store.LoadMessagesAsync(session.SessionId, default));
    }
}
