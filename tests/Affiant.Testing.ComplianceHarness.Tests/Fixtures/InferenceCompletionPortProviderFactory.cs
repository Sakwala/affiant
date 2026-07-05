namespace Affiant.Testing.ComplianceHarness.Tests.Fixtures;

using System.Collections;
using System.Runtime.CompilerServices;
using Affiant.Abstractions.Interfaces;
using Affiant.AgentFramework.Adapters;
using Affiant.SemanticKernel.Adapters;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

/// <summary>
/// xUnit [ClassData] source yielding one (portFactory, providerName) pair per interception
/// backend Affiant ships, mirroring tests/Affiant.Docket.Tests/Fixtures/DocketStoreProviderFactory.cs
/// so a third backend copies this shape (proposal affiant-maf-adapter.md §6: the cross-backend
/// ComplianceHarness gate).
///
/// portFactory builds a fresh <see cref="IInferenceCompletionPort"/> wired to a scripted,
/// in-process LLM edge (no network) that always answers the given raw JSON string — routed
/// through the real <see cref="SemanticKernelInferenceCompletionPort"/> /
/// <see cref="AgentFrameworkInferenceCompletionPort"/> translation (prompt building, markdown-fence
/// stripping, JSON parsing) rather than the assembly's generic FakeInferenceCompletionPort, so the
/// compliance gate exercises both bridges, not just the neutral pipeline behind them.
/// </summary>
public sealed class InferenceCompletionPortProviderFactory : IEnumerable<object[]>
{
    public IEnumerator<object[]> GetEnumerator()
    {
        yield return [(Func<string, IInferenceCompletionPort>)BuildSemanticKernelPort, "SemanticKernel"];
        yield return [(Func<string, IInferenceCompletionPort>)BuildAgentFrameworkPort, "AgentFramework"];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static IInferenceCompletionPort BuildSemanticKernelPort(string json)
    {
        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(new ScriptedChatCompletionService(json));
        var kernel = kernelBuilder.Build();
        return new SemanticKernelInferenceCompletionPort(
            kernel.Services, NullLogger<SemanticKernelInferenceCompletionPort>.Instance);
    }

    private static IInferenceCompletionPort BuildAgentFrameworkPort(string json) =>
        new AgentFrameworkInferenceCompletionPort(
            new ScriptedInferenceChatClient(json),
            NullLogger<AgentFrameworkInferenceCompletionPort>.Instance);

    private sealed class ScriptedChatCompletionService(string response) : IChatCompletionService
    {
        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessageContent>>(
                [new ChatMessageContent(AuthorRole.Assistant, response)]);

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("Streaming not used in structured-output inference.");
    }

    private sealed class ScriptedInferenceChatClient(string response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

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
}
