namespace Affiant.Extensions.AI.Tests.Utilities;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
/// Stateful test double for <see cref="IChatClient"/>. On the first call it requests invocation of
/// the configured function (so a <see cref="FunctionInvokingChatClient"/> above it auto-invokes the
/// tool, and therefore Affiant's wrapper); on every later call it returns plain text, which is how
/// the loop would normally end.
///
/// <see cref="CallCount"/> is the loop-continuation witness: it stays at 1 exactly when something
/// terminated the turn after the tool ran, and reaches 2 when the loop went back to the model.
/// Mirrors <c>Affiant.AgentFramework.Tests.Utilities.ScriptedChatClient</c>.
/// </summary>
internal sealed class ScriptedChatClient(
    string functionName,
    IReadOnlyDictionary<string, object?> arguments,
    string callId = "call-fake-1",
    string finalText = "(done)") : IChatClient
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>The <see cref="ChatOptions"/> the loop passed down on the most recent call.</summary>
    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var count = Interlocked.Increment(ref _callCount);
        LastOptions = options;

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
