namespace Affiant.SemanticKernel.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

/// <summary>
/// Pre-tool IFunctionInvocationFilter that captures LLM-populated tool arguments into
/// IContextFabric using ProvenanceTag.FromTool. Runs before InferenceTriggerFilter so
/// the inference prompt has access to already-decoded tool arguments. Per PRD §3.3.
///
/// NOTE: PRD §3.3 specifies ProvenanceSource.External for the captured tag. The existing
/// ProvenanceTag.FromTool factory uses ProvenanceSource.Conversation per spec §2.1 ordering
/// (LLM reads argument values FROM conversation context, making Conversation the accurate
/// classification). This story uses the existing factory as-is. See story 16.3 source-of-truth
/// anchors for rationale; a separate ProvenanceTag.FromExternalTool factory would need
/// Affiant.Abstractions changes (Story 16.1 territory) if External sourcing is later required.
/// </summary>
public sealed class ToolArgumentCaptureFilter : IFunctionInvocationFilter
{
    private readonly IContextFabric _fabric;
    private readonly IAffiantToolRegistry _registry;
    private readonly ILogger<ToolArgumentCaptureFilter> _logger;

    public ToolArgumentCaptureFilter(
        IContextFabric fabric,
        IAffiantToolRegistry registry,
        ILogger<ToolArgumentCaptureFilter> logger)
    {
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        // Only capture arguments for tools the framework tracks.
        var descriptor = _registry.Find(context.Function.Name, context.Function.PluginName);
        if (descriptor is not null && context.Arguments is not null)
        {
            foreach (var (name, value) in context.Arguments)
            {
                // PRD §3.3 says Source = External; existing FromTool factory uses Conversation per
                // spec §2.1 ordering. Keep existing for now; see story 16.3 source-of-truth anchors.
                var tag = ProvenanceTag.FromTool(toolName: context.Function.Name, confidence: 0.9f);
                _fabric.SetFieldChain(name, ProvenanceChain.From(tag));

                _logger.LogDebug(
                    "ToolArgumentCaptureFilter: captured argument {Field} from {FunctionName}",
                    name, context.Function.Name);
            }
        }

        // Always call next — this filter is non-blocking in all paths.
        await next(context);
    }
}
