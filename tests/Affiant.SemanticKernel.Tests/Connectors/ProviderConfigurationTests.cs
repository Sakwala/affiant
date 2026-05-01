using System.Runtime.CompilerServices;
using Affiant.SemanticKernel.Connectors;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace Affiant.SemanticKernel.Tests.Connectors;

public class ProviderConfigurationTests
{
    [Fact]
    public void LlmProviderConfiguration_DefaultsToGoogle()
    {
        var config = new LlmProviderConfiguration();
        Assert.Equal("google", config.Provider);
        Assert.Equal("gemini-2.5-flash", config.Model);
        Assert.Equal("", config.ApiKey);
        Assert.Null(config.Endpoint);
    }

    [Fact]
    public void AffiantProviderConfiguration_HasPrimaryWithNoSecondaryByDefault()
    {
        var config = new AffiantProviderConfiguration();
        Assert.NotNull(config.Primary);
        Assert.Null(config.Secondary);
    }

    [Fact]
    public void AffiantProviderConfiguration_PrimaryAndSecondaryAreDistinct()
    {
        var config = new AffiantProviderConfiguration
        {
            Primary = new LlmProviderConfiguration { Provider = "openai" },
            Secondary = new LlmProviderConfiguration { Provider = "google" }
        };

        Assert.Equal("openai", config.Primary.Provider);
        Assert.Equal("google", config.Secondary!.Provider);
    }

    [Fact]
    public void ProviderPair_HoldsNamedProviders()
    {
        var primary = new FakeCompletionService();
        var secondary = new FakeCompletionService();

        var pair = new ProviderPair
        {
            Primary = primary,
            PrimaryName = "openai",
            Secondary = secondary,
            SecondaryName = "google"
        };

        Assert.Equal("openai", pair.PrimaryName);
        Assert.Equal("google", pair.SecondaryName);
        Assert.Same(primary, pair.Primary);
        Assert.Same(secondary, pair.Secondary);
    }

    [Fact]
    public void ProviderPair_SecondaryIsOptional()
    {
        var primary = new FakeCompletionService();

        var pair = new ProviderPair
        {
            Primary = primary,
            PrimaryName = "openai"
        };

        Assert.Null(pair.Secondary);
        Assert.Null(pair.SecondaryName);
    }

    private sealed class FakeCompletionService : IChatCompletionService
    {
        public IReadOnlyDictionary<string, object?> Attributes =>
            new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessageContent>>([]);

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
