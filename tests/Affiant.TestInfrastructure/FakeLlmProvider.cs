namespace Affiant.TestInfrastructure;

using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

/// <summary>
/// Domain-agnostic test double for <see cref="IChatCompletionService"/>.
/// Returns shape-correct placeholder responses — enough to satisfy DI resolution
/// without issuing real HTTP calls.
///
/// Coupling note: The Phase 1 Meridian fixture included a <c>TestScenario</c> enum
/// with a <c>SearchThenProposeWorkOrder</c> value referencing the aviation domain.
/// That enum was unused in any passing test, so it was dropped here. If a host
/// test project needs scenario-specific behaviour it should subclass this provider
/// and add its own scenario enum.
/// </summary>
public sealed class FakeLlmProvider : IChatCompletionService
{
    public IReadOnlyDictionary<string, object?> Attributes { get; }
        = new Dictionary<string, object?>();

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessageContent> result =
        [
            new ChatMessageContent(AuthorRole.Assistant, "(fake-llm response)")
        ];
        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new StreamingChatMessageContent(AuthorRole.Assistant, "(fake-llm streaming response)");
    }
}
