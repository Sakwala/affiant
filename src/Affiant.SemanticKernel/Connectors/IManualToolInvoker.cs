using Microsoft.SemanticKernel;

namespace Affiant.SemanticKernel.Connectors;

public interface IManualToolInvoker
{
    Task<FunctionResultContent> CaptureAndInvokeAsync(
        FunctionCallContent functionCall, Kernel kernel, CancellationToken ct);
}
