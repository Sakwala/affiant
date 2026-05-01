namespace Affiant.SemanticKernel.Connectors;

/// <summary>
/// Top-level provider configuration supporting primary/secondary failover.
/// Bound from <c>Affiant:Providers</c> in appsettings.json.
/// When this section is absent, the host falls back to a legacy single-provider primary.
/// </summary>
public class AffiantProviderConfiguration
{
    public LlmProviderConfiguration Primary { get; set; } = new();
    public LlmProviderConfiguration? Secondary { get; set; }
}
