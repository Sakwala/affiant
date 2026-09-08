namespace Affiant.Core.Services;

using System.Diagnostics;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stateless orchestrator for pre-tool structured-output inference.
/// Builds an <see cref="InferenceCompletionRequest"/>, calls the port, and forwards the
/// resulting JSON to <see cref="TaskInferenceStep"/> for confidence-based merge.
/// Idempotency (once-per-turn) is the caller's responsibility — see 16.3's InferenceTriggerFilter.
///
/// <para>
/// Every failure of the inference call degrades to an empty result, with one exception: the
/// caller's own cancellation, which propagates. A cancellation the caller did not ask for — a
/// provider's timeout — is a provider failure like any other (affiant#102).
/// </para>
/// </summary>
public sealed class TaskInferenceRunner
{
    private readonly IInferenceCompletionPort _port;
    private readonly IContextFabric _fabric;
    private readonly TaskInferenceStep _mergeStep;
    private readonly ILogger<TaskInferenceRunner> _logger;

    public TaskInferenceRunner(
        IInferenceCompletionPort port,
        IContextFabric fabric,
        TaskInferenceStep mergeStep,
        ILogger<TaskInferenceRunner> logger)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _mergeStep = mergeStep ?? throw new ArgumentNullException(nameof(mergeStep));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TaskInferenceResult> RunAsync(
        ITaskInferenceStrategy strategy,
        IReadOnlyList<AffiantChatMessage> history,
        string functionName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new InferenceCompletionRequest(history, strategy, functionName, arguments);
            var json = await _port.CompleteStructuredAsync(request, cancellationToken).ConfigureAwait(false);
            var result = await _mergeStep.ExecuteAsync(strategy, json, cancellationToken).ConfigureAwait(false);

            Activity.Current?.AddEvent(new ActivityEvent(
                "inference.completed",
                tags: new ActivityTagsCollection
                {
                    { L2TelemetryKeys.FieldsMerged, result.MergedFields.Count(kv => kv.Value.Merged) },
                    { L2TelemetryKeys.FieldsInResponse, result.FieldsInLlmResponse },
                    { L2TelemetryKeys.FieldsInSchema, result.TotalFieldsInSchema },
                }));

            return result;
        }
        // affiant#102: only the caller's own cancellation propagates. A provider timeout arrives as
        // a TaskCanceledException — an OperationCanceledException — with the caller's token never
        // signalled; rethrowing that broke the turn on the most common provider failure of all,
        // which is exactly what the fail-safe below exists to prevent. It falls to the general
        // catch instead: a warning, an inference.failed event, and an empty result.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Activity.Current?.AddEvent(new ActivityEvent(
                "inference.failed",
                tags: new ActivityTagsCollection
                {
                    { L2TelemetryKeys.FunctionName, functionName },
                    { L2TelemetryKeys.ErrorKind, "cancelled" },
                }));
            throw;
        }
        catch (JsonException ex)
        {
            Activity.Current?.AddEvent(new ActivityEvent(
                "inference.failed",
                tags: new ActivityTagsCollection
                {
                    { L2TelemetryKeys.FunctionName, functionName },
                    { L2TelemetryKeys.ErrorKind, "json_parse" },
                }));
            _logger.LogWarning(ex, "TaskInferenceRunner: inference failed for {FunctionName}; returning empty result", functionName);
            return new TaskInferenceResult(
                TotalFieldsInSchema: strategy.Fields.Count,
                FieldsInLlmResponse: 0,
                MergedFields: new Dictionary<string, TaskInferenceMergeOutcome>());
        }
        catch (Exception ex)
        {
            Activity.Current?.AddEvent(new ActivityEvent(
                "inference.failed",
                tags: new ActivityTagsCollection
                {
                    { L2TelemetryKeys.FunctionName, functionName },
                    { L2TelemetryKeys.ErrorKind, "provider_outage" },
                }));
            _logger.LogWarning(ex, "TaskInferenceRunner: inference failed for {FunctionName}; returning empty result", functionName);
            return new TaskInferenceResult(
                TotalFieldsInSchema: strategy.Fields.Count,
                FieldsInLlmResponse: 0,
                MergedFields: new Dictionary<string, TaskInferenceMergeOutcome>());
        }
    }
}
