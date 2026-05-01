using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Affiant.SemanticKernel.Connectors;

// Uses SK kernel builder extension methods to avoid direct constructor dependencies
// on connector-specific types that may be internal to each connector package.
public static class ChatCompletionFactory
{
    public static IChatCompletionService Create(LlmProviderConfiguration config)
    {
        var kernelBuilder = Kernel.CreateBuilder();

        switch (config.Provider.ToLowerInvariant())
        {
            case "openai":
                kernelBuilder.AddOpenAIChatCompletion(
                    modelId: config.Model,
                    apiKey: config.ApiKey);
                break;

            case "azure-openai":
                kernelBuilder.AddAzureOpenAIChatCompletion(
                    deploymentName: config.Model,
                    endpoint: config.Endpoint!,
                    apiKey: config.ApiKey);
                break;

            case "openai-compatible":
                kernelBuilder.AddOpenAIChatCompletion(
                    modelId: config.Model,
                    apiKey: config.ApiKey,
                    httpClient: new HttpClient { BaseAddress = new Uri(config.Endpoint!) });
                break;

            case "anthropic":
                throw new InvalidOperationException(
                    "The 'anthropic' provider requires an unofficial connector package. " +
                    "Use 'openai-compatible' with Anthropic's API endpoint instead.");

            case "google":
#pragma warning disable SKEXP0070
                kernelBuilder.AddGoogleAIGeminiChatCompletion(
                    modelId: config.Model,
                    apiKey: config.ApiKey);
#pragma warning restore SKEXP0070
                break;

            case "ollama":
#pragma warning disable SKEXP0070
                kernelBuilder.AddOllamaChatCompletion(
                    modelId: config.Model,
                    endpoint: new Uri(config.Endpoint ?? "http://localhost:11434"));
#pragma warning restore SKEXP0070
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown LLM provider: '{config.Provider}'. " +
                    "Supported: openai, azure-openai, openai-compatible, google, ollama");
        }

        var kernel = kernelBuilder.Build();
        return kernel.GetRequiredService<IChatCompletionService>();
    }
}
