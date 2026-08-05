using Affiant.Abstractions.Models;
using Affiant.EntityFramework.Stores;
using Affiant.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Affiant.EntityFramework.Tests;

/// <summary>
/// Runs the same case set as <see cref="SharedChatSessionStoreTests"/> against
/// <see cref="PostgresChatSessionStore"/>, via the assembly's existing
/// <c>[Collection("Postgres")]</c>/<see cref="PostgresFixture"/> pair — the same fixture
/// <see cref="PostgresChatSessionStoreAppendTests"/> already shares — rather than through
/// <see cref="Fixtures.ChatSessionStoreProviderFactory"/>'s <c>[ClassData]</c>. Deliberately
/// separate: <c>[ClassData]</c> enumerates synchronously at test-discovery time, so giving it a
/// Postgres slot means a second, uncoordinated Testcontainers spin-up racing this assembly's one
/// collection-fixture container for the Docker daemon — reproducibly starved the sibling
/// container's own startup under load (<c>RegexMatchTimeoutException</c> inside Testcontainers'
/// own image-name match). Reusing the shared fixture keeps exactly one Postgres container for the
/// whole assembly.
/// </summary>
[Collection("Postgres")]
public sealed class SharedChatSessionStoreTestsPostgres(PostgresFixture postgres) : IAsyncLifetime
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

    // ── Case 1: Session round-trip ────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ThenGetAsync_RoundTripsSession()
    {
        var created = await _store.CreateAsync("tenant-001", "user-001", CancellationToken.None);
        var loaded = await _store.GetAsync(created.SessionId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(created.SessionId, loaded.SessionId);
        Assert.Equal("tenant-001", loaded.TenantId);
        Assert.Equal("user-001", loaded.UserId);
    }

    // ── Case 2: Missing session ───────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ForMissingSession_ReturnsNull()
    {
        var loaded = await _store.GetAsync("does-not-exist", CancellationToken.None);

        Assert.Null(loaded);
    }

    // ── Case 3: Message round-trip, incl. tool-call/tool-result metadata ─

    [Fact]
    public async Task SaveMessagesAsync_RoundTrips_ToolCallAndResultMetadata()
    {
        var session = await _store.CreateAsync("tenant-001", "user-001", CancellationToken.None);

        var toolCall = new AffiantChatMessage("assistant", string.Empty)
        {
            AuthorName = "agent",
            ToolCallId = "call_001",
            FunctionName = "LookupRecord",
            ArgumentsJson = """{"recordId":"REC-42"}""",
        };
        var toolResult = new AffiantChatMessage("tool", "Record REC-42 found.")
        {
            ToolCallId = "call_001",
            FunctionName = "LookupRecord",
        };

        var messages = new AffiantChatMessage[]
        {
            new("system", "You are a helpful assistant."),
            new("user", "look up REC-42"),
            toolCall,
            toolResult,
            new("assistant", "Record REC-42 was found."),
            new("user", "thanks"),
        };

        await _store.SaveMessagesAsync(session.SessionId, messages, CancellationToken.None);
        var loaded = await _store.LoadMessagesAsync(session.SessionId, CancellationToken.None);

        Assert.Equal(6, loaded.Count);
        Assert.Equal(
            ["system", "user", "assistant", "tool", "assistant", "user"],
            loaded.Select(m => m.Role));

        Assert.Equal("You are a helpful assistant.", loaded[0].Content);
        Assert.Equal("look up REC-42", loaded[1].Content);
        Assert.Equal("Record REC-42 was found.", loaded[4].Content);
        Assert.Equal("thanks", loaded[5].Content);

        Assert.Equal("LookupRecord", loaded[2].FunctionName);
        Assert.Equal("call_001", loaded[2].ToolCallId);
        Assert.Contains("REC-42", loaded[2].ArgumentsJson);

        Assert.Equal("call_001", loaded[3].ToolCallId);
        Assert.Equal("LookupRecord", loaded[3].FunctionName);
        Assert.Equal("Record REC-42 found.", loaded[3].Content);
    }

    // ── Case 4: Full-replace semantics ────────────────────────────────────

    [Fact]
    public async Task SaveMessagesAsync_ReplaceAll_DropsPriorMessages()
    {
        var session = await _store.CreateAsync("tenant-001", "user-001", CancellationToken.None);

        await _store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "first"),
            new AffiantChatMessage("assistant", "second"),
            new AffiantChatMessage("user", "third"),
        ], CancellationToken.None);

        await _store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("system", "fresh prompt"),
            new AffiantChatMessage("user", "fresh start"),
        ], CancellationToken.None);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, CancellationToken.None);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("fresh prompt", loaded[0].Content);
        Assert.Equal("fresh start", loaded[1].Content);
    }

    // ── Case 5: AppendMessagesAsync semantics ─────────────────────────────

    [Fact]
    public async Task AppendMessagesAsync_ToEmptySession_PersistsMessagesInOrder()
    {
        var session = await _store.CreateAsync("tenant-001", "user-001", CancellationToken.None);

        await _store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "hello"),
            new AffiantChatMessage("assistant", "world"),
        ], CancellationToken.None);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, CancellationToken.None);
        Assert.Equal(["hello", "world"], loaded.Select(m => m.Content));
    }

    [Fact]
    public async Task AppendMessagesAsync_AfterExistingMessages_AppendsWithoutTouchingExisting()
    {
        var session = await _store.CreateAsync("tenant-001", "user-001", CancellationToken.None);
        await _store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("system", "prompt"),
            new AffiantChatMessage("user", "first"),
        ], CancellationToken.None);

        await _store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("assistant", "second"),
            new AffiantChatMessage("user", "third"),
        ], CancellationToken.None);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, CancellationToken.None);
        Assert.Equal(["prompt", "first", "second", "third"], loaded.Select(m => m.Content));
    }

    [Fact]
    public async Task AppendMessagesAsync_WithEmptyList_IsNoOp()
    {
        var session = await _store.CreateAsync("tenant-001", "user-001", CancellationToken.None);
        await _store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "only"),
        ], CancellationToken.None);

        await _store.AppendMessagesAsync(session.SessionId, [], CancellationToken.None);

        var loaded = await _store.LoadMessagesAsync(session.SessionId, CancellationToken.None);
        Assert.Single(loaded);
    }

    // ── Case 6: DeleteAsync ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesSessionAndMessages()
    {
        var session = await _store.CreateAsync("tenant-001", "user-001", CancellationToken.None);
        await _store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "hi"),
        ], CancellationToken.None);

        await _store.DeleteAsync(session.SessionId, CancellationToken.None);

        Assert.Null(await _store.GetAsync(session.SessionId, CancellationToken.None));
        Assert.Empty(await _store.LoadMessagesAsync(session.SessionId, CancellationToken.None));
    }
}
