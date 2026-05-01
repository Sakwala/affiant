namespace Affiant.SemanticKernel.Connectors;

public class LlmProviderConfiguration
{
    public string Provider { get; set; } = "google";
    public string Model { get; set; } = "gemini-2.5-flash";
    public string ApiKey { get; set; } = "";
    public string? Endpoint { get; set; }
}
