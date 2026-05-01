using Microsoft.SemanticKernel.ChatCompletion;

namespace Affiant.SemanticKernel.Connectors;

public class ProviderPair
{
    public required IChatCompletionService Primary { get; init; }
    public IChatCompletionService? Secondary { get; init; }
    public required string PrimaryName { get; init; }
    public string? SecondaryName { get; init; }
}
