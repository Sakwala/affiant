namespace Affiant.Core.Filters;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

/// <summary>
/// Thin IAutoFunctionInvocationFilter adapter that delegates to TaskInferenceStep.
/// Renamed from TaskInferenceFilter in Story 16.4 (2026-05-16) — see L2 PRD §10.4 — to disambiguate from the new pre-tool InferenceTriggerFilter (16.3).
/// Fires after each auto-invoked function during LLM chat completion. If the function
/// result is a JSON object containing field values with "value"/"confidence" properties,
/// the result is forwarded to TaskInferenceStep for confidence-based merging.
/// Non-JSON results are silently skipped so the filter is safe to register globally.
/// </summary>
public sealed class TaskInferenceMergeFilter : IAutoFunctionInvocationFilter
{
    private readonly TaskInferenceStep _step;
    private readonly ILogger<TaskInferenceMergeFilter> _logger;

    public TaskInferenceMergeFilter(
        TaskInferenceStep step,
        ILogger<TaskInferenceMergeFilter> logger)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
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

        try
        {
            using var doc = JsonDocument.Parse(resultString);
            var result = await _step.ExecuteAsync(doc.RootElement);

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
