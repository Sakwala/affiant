namespace Affiant.Core.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pre-tool filter that captures LLM-populated tool arguments into IContextFabric using
/// ProvenanceTag.FromTool. Runs before InferenceTriggerFilter so the inference prompt has access
/// to already-decoded tool arguments. Per L2 PRD §3.3.
///
/// NOTE: PRD §3.3 specifies ProvenanceSource.External for the captured tag. The existing
/// ProvenanceTag.FromTool factory uses ProvenanceSource.Conversation per spec §2.1 ordering
/// (the LLM reads argument values FROM conversation context, making Conversation the accurate
/// classification). This uses the existing factory as-is.
/// </summary>
public sealed class ToolArgumentCaptureFilter : IToolInvocationFilter
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

    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        // Only capture arguments for tools the framework tracks.
        var pluginName = string.IsNullOrEmpty(context.PluginName) ? null : context.PluginName;
        var descriptor = _registry.Find(context.FunctionName, pluginName);
        if (descriptor is not null && context.Arguments is not null)
        {
            foreach (var (name, _) in context.Arguments)
            {
                var tag = ProvenanceTag.FromTool(toolName: context.FunctionName, confidence: 0.9f);
                _fabric.SetFieldChain(name, ProvenanceChain.From(tag));

                _logger.LogDebug(
                    "ToolArgumentCaptureFilter: captured argument {Field} from {FunctionName}",
                    name, context.FunctionName);
            }
        }

        // Always call next — this filter is non-blocking in all paths.
        await next(context);
    }
}
