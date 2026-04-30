namespace Affiant.Abstractions.Interfaces;

public interface IConnectorCapabilities
{
    bool SupportsAutoFunctionInvocationFilter { get; }
    bool SupportsStreamingFunctionCalls { get; }
    bool SupportsStructuredOutput { get; }
    bool SupportsParallelToolCalls { get; }
}
