namespace Affiant.Core.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Post-tool completion-stage filter that delegates to TaskInferenceStep. Fires after each
/// auto-invoked tool. If the tool has a registered write descriptor (an AffiantToolDescriptor
/// with an InferenceStrategy), and the tool result is a JSON object containing field values with
/// "value"/"confidence" properties, the result is forwarded to TaskInferenceStep for
/// confidence-based merging. Tools without a write descriptor (read tools, unregistered tools)
/// are skipped. Non-JSON results are logged and skipped (see remarks) so the filter is safe to
/// register globally.
///
/// <para>
/// <b>Merge-failure policy (area-3 P2 ruling 3, gate ruling "surface-and-continue"):</b> this filter
/// runs strictly after the tool already produced its result. Any non-cancellation exception from the
/// merge attempt — malformed JSON, or a bug in <see cref="TaskInferenceStep.ExecuteAsync"/>/the
/// resolved <see cref="ITaskInferenceStrategy"/> — must never discard that result, never cause the
/// tool to be re-executed, and never be reported to the model as a tool failure (V5: previously an
/// uncaught non-<see cref="JsonException"/> here propagated into <c>ToolErrorFilter</c>'s
/// retry-the-whole-chain logic on MAF, or raw into SK's auto-invocation loop — see
/// <c>Affiant.SemanticKernel.Filters.BridgeStages</c>). Every such exception is logged and emitted
/// as an <c>affiant.extractor.failed</c> OTel event; the pipeline continues with the tool's genuine
/// result untouched.
/// </para>
/// </summary>
public sealed class TaskInferenceMergeFilter : ICompletionStageFilter
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Surface-and-continue (area-3 P2 ruling 3): the tool's result (already set on
            // context.Result by the time this runs) is never touched. JsonException (result is not
            // structured JSON with field/confidence pairs — the common, expected case for most tool
            // results) and any other merge-time exception are both handled identically: logged +
            // OTel, never surfaced to the model, never retried.
            AffiantTelemetry.RecordExtractorFailedEvent(nameof(TaskInferenceMergeFilter), context.FunctionName, ex);
            _logger.LogError(ex,
                "TaskInferenceMergeFilter failed to merge inference results for tool {FunctionName} — " +
                "the tool's result is preserved and NOT reported as a failure to the model " +
                "(surface-and-continue)",
                context.FunctionName);
        }
    }
}
