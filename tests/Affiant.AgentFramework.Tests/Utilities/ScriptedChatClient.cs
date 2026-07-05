namespace Affiant.AgentFramework.Tests.Utilities;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
/// Stateful test double for <see cref="IChatClient"/>. On the first call, requests invocation of
/// the configured function (so <see cref="FunctionInvokingChatClient"/> auto-invokes it through
/// the middleware chain); on subsequent calls, returns a plain text response to end the loop.
/// Mirrors Affiant.SemanticKernel.Tests' FakeLlmProvider pattern for the MAF side.
/// </summary>
internal sealed class ScriptedChatClient(
    string functionName,
    IReadOnlyDictionary<string, object?> arguments,
    string callId = "call-fake-1",
    string finalText = "(done)") : IChatClient
{
    private int _callCount;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var count = Interlocked.Increment(ref _callCount);

        ChatMessage message = count == 1
            ? new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(callId, functionName, new Dictionary<string, object?>(arguments))])
            : new ChatMessage(ChatRole.Assistant, finalText);

        return Task.FromResult(new ChatResponse(message));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
            yield return new ChatResponseUpdate(message.Role, message.Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>No-op IChatClient — used only where an AIAgent instance must exist structurally.</summary>
internal sealed class NoOpChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "(noop)")));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
            yield return new ChatResponseUpdate(message.Role, message.Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
