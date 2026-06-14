namespace Affiant.Core.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

/// <summary>
/// Thin IAutoFunctionInvocationFilter adapter that delegates to TaskInferenceStep.
/// Renamed from TaskInferenceFilter in Story 16.4 (2026-05-16) — see L2 PRD §10.4 — to
/// disambiguate from the new pre-tool InferenceTriggerFilter (16.3).
/// Fires after each auto-invoked function during LLM chat completion. If the function
/// has a registered write descriptor (i.e. an AffiantToolDescriptor with an InferenceStrategy),
/// and the function result is a JSON object containing field values with "value"/"confidence"
/// properties, the result is forwarded to TaskInferenceStep for confidence-based merging.
/// Functions without a write descriptor (read tools, unregistered functions) are skipped.
/// Non-JSON results are silently skipped so the filter is safe to register globally.
/// </summary>
public sealed class TaskInferenceMergeFilter : IAutoFunctionInvocationFilter
{
    private readonly TaskInferenceStep _step;
    private readonly IAffiantToolRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TaskInferenceMergeFilter> _logger;

    public TaskInferenceMergeFilter(
        TaskInferenceStep step,
        IAffiantToolRegistry registry,
        IServiceProvider serviceProvider,
        ILogger<TaskInferenceMergeFilter> logger)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context,
        Func<AutoFunctionInvocationContext, Task> next)
    {
        await next(context);

        var resultString = context.Result?.ToString();
        if (string.IsNullOrEmpty(resultString))
            return;

        // Resolve the strategy from the descriptor. Read tools and unregistered functions
        // have no InferenceStrategy and are skipped — post-tool merge only applies to
        // write tools whose output may carry confidence-tagged JSON fields.
        var descriptor = _registry.Find(context.Function.Name, context.Function.PluginName);
        if (descriptor?.InferenceStrategy == null)
            return;

        var strategy = (ITaskInferenceStrategy)_serviceProvider.GetRequiredService(descriptor.InferenceStrategy);

        try
        {
            using var doc = JsonDocument.Parse(resultString);
            var result = await _step.ExecuteAsync(strategy, doc.RootElement);

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
