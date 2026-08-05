using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.EntityFramework.Tests.Fixtures;
using Xunit;

namespace Affiant.EntityFramework.Tests;

/// <summary>
/// Shared invariant tests that run against the two no-external-dependency
/// <see cref="IChatSessionStore"/> implementations (InMemory, SQLite) via
/// <see cref="ChatSessionStoreProviderFactory"/> — the chat-store analogue of
/// <c>Affiant.Docket.Tests.SharedDocketStoreTests</c> (Area-5 P4 item I).
/// <see cref="SharedChatSessionStoreTestsPostgres"/> runs the identical case set against Postgres
/// via the assembly's existing <c>[Collection("Postgres")]</c> fixture rather than through this
/// <c>[ClassData]</c> factory — see that factory's own remarks for why. Before this pair of
/// suites, the framework had no cross-backend parity coverage for chat sessions at all: SQLite-only
/// round-trip coverage lived in <see cref="ChatSessionRoundTripTests"/>, and Postgres behavioral
/// coverage existed only as a downstream host's own private test suite — framework test debt
/// privatized to one host's diligence (Area-5 evidence pack <c>area-5-store-parity.md</c> §4). This
/// suite folds that behavior upstream with domain-neutral payloads in place of the host's
/// domain-specific ones: <see cref="SaveMessagesAsync_RoundTrips_ToolCallAndResultMetadata"/>
/// covers the tool-call/tool-result round-trip and ordinal-order behavior the host suite proved on
/// Postgres only; <see cref="SaveMessagesAsync_ReplaceAll_DropsPriorMessages"/> covers its
/// full-replace assertion — both as a stronger, every-backend-covered pair of suites rather than a
/// Postgres-only duplicate, closing the Postgres in-framework gap the evidence pack flagged for
/// exactly this behavior.
///
///   Case 1 — Session round-trip: CreateAsync/GetAsync.
///   Case 2 — Missing session: GetAsync returns null.
///   Case 3 — SaveMessagesAsync round-trip, including tool-call/tool-result metadata fields.
///   Case 4 — SaveMessagesAsync full-replace semantics: a second call drops everything the first wrote.
///   Case 5 — AppendMessagesAsync: empty session, after existing messages, empty-list no-op.
///   Case 6 — DeleteAsync removes both the session and its messages.
/// </summary>
public sealed class SharedChatSessionStoreTests
{
    // ── Case 1: Session round-trip ────────────────────────────────────────

    [Theory]
    [ClassData(typeof(ChatSessionStoreProviderFactory))]
    public async Task CreateAsync_ThenGetAsync_RoundTripsSession(
        IChatSessionStore store, string providerName)
    {
        Assert.NotEmpty(providerName);

        var created = await store.CreateAsync("tenant-001", "user-001", CancellationToken.None);
        var loaded = await store.GetAsync(created.SessionId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(created.SessionId, loaded.SessionId);
        Assert.Equal("tenant-001", loaded.TenantId);
        Assert.Equal("user-001", loaded.UserId);
    }

    // ── Case 2: Missing session ───────────────────────────────────────────

    [Theory]
    [ClassData(typeof(ChatSessionStoreProviderFactory))]
    public async Task GetAsync_ForMissingSession_ReturnsNull(
        IChatSessionStore store, string providerName)
    {
        Assert.NotEmpty(providerName);

        var loaded = await store.GetAsync("does-not-exist", CancellationToken.None);

        Assert.Null(loaded);
    }

    // ── Case 3: Message round-trip, incl. tool-call/tool-result metadata ─

    [Theory]
    [ClassData(typeof(ChatSessionStoreProviderFactory))]
    public async Task SaveMessagesAsync_RoundTrips_ToolCallAndResultMetadata(
        IChatSessionStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var session = await store.CreateAsync("tenant-001", "user-001", CancellationToken.None);

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

        await store.SaveMessagesAsync(session.SessionId, messages, CancellationToken.None);
        var loaded = await store.LoadMessagesAsync(session.SessionId, CancellationToken.None);

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

    [Theory]
    [ClassData(typeof(ChatSessionStoreProviderFactory))]
    public async Task SaveMessagesAsync_ReplaceAll_DropsPriorMessages(
        IChatSessionStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var session = await store.CreateAsync("tenant-001", "user-001", CancellationToken.None);

        await store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "first"),
            new AffiantChatMessage("assistant", "second"),
            new AffiantChatMessage("user", "third"),
        ], CancellationToken.None);

        await store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("system", "fresh prompt"),
            new AffiantChatMessage("user", "fresh start"),
        ], CancellationToken.None);

        var loaded = await store.LoadMessagesAsync(session.SessionId, CancellationToken.None);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("fresh prompt", loaded[0].Content);
        Assert.Equal("fresh start", loaded[1].Content);
    }

    // ── Case 5: AppendMessagesAsync semantics ─────────────────────────────

    [Theory]
    [ClassData(typeof(ChatSessionStoreProviderFactory))]
    public async Task AppendMessagesAsync_ToEmptySession_PersistsMessagesInOrder(
        IChatSessionStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var session = await store.CreateAsync("tenant-001", "user-001", CancellationToken.None);

        await store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "hello"),
            new AffiantChatMessage("assistant", "world"),
        ], CancellationToken.None);

        var loaded = await store.LoadMessagesAsync(session.SessionId, CancellationToken.None);
        Assert.Equal(["hello", "world"], loaded.Select(m => m.Content));
    }

    [Theory]
    [ClassData(typeof(ChatSessionStoreProviderFactory))]
    public async Task AppendMessagesAsync_AfterExistingMessages_AppendsWithoutTouchingExisting(
        IChatSessionStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var session = await store.CreateAsync("tenant-001", "user-001", CancellationToken.None);
        await store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("system", "prompt"),
            new AffiantChatMessage("user", "first"),
        ], CancellationToken.None);

        await store.AppendMessagesAsync(session.SessionId, [
            new AffiantChatMessage("assistant", "second"),
            new AffiantChatMessage("user", "third"),
        ], CancellationToken.None);

        var loaded = await store.LoadMessagesAsync(session.SessionId, CancellationToken.None);
        Assert.Equal(["prompt", "first", "second", "third"], loaded.Select(m => m.Content));
    }

    [Theory]
    [ClassData(typeof(ChatSessionStoreProviderFactory))]
    public async Task AppendMessagesAsync_WithEmptyList_IsNoOp(
        IChatSessionStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var session = await store.CreateAsync("tenant-001", "user-001", CancellationToken.None);
        await store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "only"),
        ], CancellationToken.None);

        await store.AppendMessagesAsync(session.SessionId, [], CancellationToken.None);

        var loaded = await store.LoadMessagesAsync(session.SessionId, CancellationToken.None);
        Assert.Single(loaded);
    }

    // ── Case 6: DeleteAsync ────────────────────────────────────────────────

    [Theory]
    [ClassData(typeof(ChatSessionStoreProviderFactory))]
    public async Task DeleteAsync_RemovesSessionAndMessages(
        IChatSessionStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var session = await store.CreateAsync("tenant-001", "user-001", CancellationToken.None);
        await store.SaveMessagesAsync(session.SessionId, [
            new AffiantChatMessage("user", "hi"),
        ], CancellationToken.None);

        await store.DeleteAsync(session.SessionId, CancellationToken.None);

        Assert.Null(await store.GetAsync(session.SessionId, CancellationToken.None));
        Assert.Empty(await store.LoadMessagesAsync(session.SessionId, CancellationToken.None));
    }
}
