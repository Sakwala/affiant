using Affiant.Abstractions.Interfaces;

namespace Affiant.SemanticKernel.Connectors.Capabilities;

internal sealed class GoogleGeminiCapabilities : IConnectorCapabilities
{
    public bool SupportsAutoFunctionInvocationFilter => true;
    public bool SupportsStreamingFunctionCalls => false;
    public bool SupportsStructuredOutput => true;
    public bool SupportsParallelToolCalls => true;
}
