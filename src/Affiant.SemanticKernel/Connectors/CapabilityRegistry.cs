using Affiant.Abstractions.Interfaces;
using Affiant.SemanticKernel.Connectors.Capabilities;

namespace Affiant.SemanticKernel.Connectors;

public class CapabilityRegistry
{
    private readonly Dictionary<string, IConnectorCapabilities> _capabilities =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new OpenAiCapabilities(),
            ["azure-openai"] = new AzureOpenAiCapabilities(),
            ["google"] = new GoogleGeminiCapabilities(),
            ["ollama"] = new OllamaCapabilities(),
            ["openai-compatible"] = new OpenAiCompatibleCapabilities()
        };

    // Throws KeyNotFoundException for unknown providers — fail fast at startup.
    public IConnectorCapabilities Resolve(string providerName) =>
        _capabilities[providerName];
}
