using Affiant.Abstractions.Interfaces;

namespace Affiant.SemanticKernel.Connectors.Capabilities;

internal sealed class OpenAiCompatibleCapabilities : IConnectorCapabilities
{
    public bool SupportsAutoFunctionInvocationFilter => true;
    public bool SupportsStreamingFunctionCalls => true;
    public bool SupportsStructuredOutput => true;
    public bool SupportsParallelToolCalls => true;
}
