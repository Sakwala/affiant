namespace Affiant.Core.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Post-tool completion-stage filter that delegates to TaskInferenceStep. Fires after each
/// auto-invoked tool. If the tool has a registered write descriptor (an AffiantToolDescriptor
/// with an InferenceStrategy), and the tool result is a JSON object containing field values with
/// "value"/"confidence" properties, the result is forwarded to TaskInferenceStep for
/// confidence-based merging. Tools without a write descriptor (read tools, unregistered tools)
/// are skipped. Non-JSON results are silently skipped so the filter is safe to register globally.
/// </summary>
public sealed class TaskInferenceMergeFilter : IToolInvocationFilter
{
    private readonly TaskInferenceStep _step;
    private readonly IAffiantToolRegistry _registry;
    private readonly ILogger<TaskInferenceMergeFilter> _logger;

    public TaskInferenceMergeFilter(
        TaskInferenceStep step,
        IAffiantToolRegistry registry,
        ILogger<TaskInferenceMergeFilter> logger)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        await next(context);

        var resultString = context.Result as string ?? context.Result?.ToString();
        if (string.IsNullOrEmpty(resultString))
            return;

        // Resolve the strategy from the descriptor. Read tools and unregistered tools have no
        // InferenceStrategy and are skipped — post-tool merge only applies to write tools whose
        // output may carry confidence-tagged JSON fields.
        var pluginName = string.IsNullOrEmpty(context.PluginName) ? null : context.PluginName;
        var descriptor = _registry.Find(context.FunctionName, pluginName);
        if (descriptor?.InferenceStrategy == null)
            return;

        var strategy = (ITaskInferenceStrategy)context.Services.GetRequiredService(descriptor.InferenceStrategy);

        try
        {
            using var doc = JsonDocument.Parse(resultString);
            var result = await _step.ExecuteAsync(strategy, doc.RootElement, cancellationToken);

            var mergedCount = result.MergedFields.Count(kv => kv.Value.Merged);
            if (mergedCount > 0)
            {
                _logger.LogInformation(
                    "Task inference merged {MergedCount} of {TotalCount} schema fields",
                    mergedCount, result.TotalFieldsInSchema);
            }
        }
        catch (JsonException)
        {
            // Result is not structured JSON with field/confidence pairs; skip silently.
        }
    }
}
