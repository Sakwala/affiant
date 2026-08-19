namespace Affiant.Testing.ComplianceHarness.Tests.Fixtures;

using System.Collections;
using System.Runtime.CompilerServices;
using Affiant.Abstractions.Interfaces;
using Affiant.AgentFramework.Adapters;
using Affiant.Extensions.AI.Adapters;
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
/// <see cref="AgentFrameworkInferenceCompletionPort"/> /
/// <see cref="ExtensionsAIInferenceCompletionPort"/> translation (prompt building, markdown-fence
/// stripping, JSON parsing) rather than the assembly's generic FakeInferenceCompletionPort, so the
/// compliance gate exercises all three bridges, not just the neutral pipeline behind them.
///
/// <para>
/// <b>The third backend (M.E.AI adapter design brief decision 7, 2026-08-20:
/// <c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>).</b> Adding
/// <c>Affiant.Extensions.AI</c> to the compliance gate is exactly the one <c>yield return</c> this
/// file's shape was designed to cost — the harness itself needed no change, because
/// <c>ComplianceHarness</c> only ever touches <c>Abstractions</c>/<c>Core</c> contracts. Every
/// <c>[Theory]</c> in <see cref="CrossBackendComplianceParityTests"/> therefore runs three times
/// instead of two, and the new adapter inherits the whole discoverability / fixture-execution /
/// substance-gate suite for free.
/// </para>
///
/// <para>
/// <c>ExtensionsAIInferenceCompletionPort</c> and <c>AgentFrameworkInferenceCompletionPort</c> are
/// currently line-for-line siblings (the M.E.AI port is a deliberate copy — design brief decision 3,
/// "copy, don't reference", because a <c>ProjectReference</c> between two adapter packages would
/// amend the layering invariant Area-8 just re-established). They are both listed here anyway, and
/// deliberately so: the copies are expected to be collapsed post-beta, and until then this gate is
/// what would catch either one drifting from the shared behaviour.
/// </para>
/// </summary>
public sealed class InferenceCompletionPortProviderFactory : IEnumerable<object[]>
{
    public IEnumerator<object[]> GetEnumerator()
    {
        yield return [(Func<string, IInferenceCompletionPort>)BuildSemanticKernelPort, "SemanticKernel"];
        yield return [(Func<string, IInferenceCompletionPort>)BuildAgentFrameworkPort, "AgentFramework"];
        yield return [(Func<string, IInferenceCompletionPort>)BuildExtensionsAIPort, "ExtensionsAI"];
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

    // Same scripted IChatClient edge as the MAF port above — both take an IChatClient directly, so
    // the only difference between these two rows is which adapter assembly's translation code runs.
    private static IInferenceCompletionPort BuildExtensionsAIPort(string json) =>
        new ExtensionsAIInferenceCompletionPort(
            new ScriptedInferenceChatClient(json),
            NullLogger<ExtensionsAIInferenceCompletionPort>.Instance);

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
