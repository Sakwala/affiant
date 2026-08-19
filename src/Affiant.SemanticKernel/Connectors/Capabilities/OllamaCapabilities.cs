using Affiant.Abstractions.Interfaces;

namespace Affiant.SemanticKernel.Connectors.Capabilities;

internal sealed class OllamaCapabilities : IConnectorCapabilities
{
    public bool SupportsAutoFunctionInvocationFilter => true;
    public bool SupportsStreamingFunctionCalls => false;
    public bool SupportsStructuredOutput => false;
    public bool SupportsParallelToolCalls => false;
}
