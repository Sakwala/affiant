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
            foreach (var (name, value) in context.Arguments)
            {
                // AF-1: only an argument that carries a value is tagged. An argument the model
                // passed as null is a field with nothing behind it, and the projection swears it
                // Empty at confidence 0 — which is what makes the aggregate 0 and the empty-field
                // count 1. Tagging it Conversation at 0.9 swore that the conversation had said
                // something, when what it had said was nothing.
                if (value is null)
                {
                    _logger.LogDebug(
                        "ToolArgumentCaptureFilter: argument {Field} from {FunctionName} carries no " +
                        "value; it is sworn Empty rather than tagged",
                        name, context.FunctionName);
                    continue;
                }

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
