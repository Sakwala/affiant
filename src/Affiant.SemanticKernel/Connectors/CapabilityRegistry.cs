using Affiant.Abstractions.Interfaces;
using Affiant.SemanticKernel.Connectors.Capabilities;

namespace Affiant.SemanticKernel.Connectors;

/// <summary>
/// Maps a provider name to its <see cref="IConnectorCapabilities"/> quirk profile. Registered by
/// concrete type in DI (<c>AddAffiantSemanticKernel</c>), so a host may resolve it and call
/// <see cref="Resolve"/> directly. The per-provider leaf classes behind it are deliberately
/// <c>internal</c>: <see cref="Resolve"/> only ever hands back the interface, so no adopter code
/// path can observe or need their concrete names.
/// </summary>
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
