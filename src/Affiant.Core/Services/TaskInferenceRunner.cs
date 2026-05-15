namespace Affiant.Core.Services;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

/// <summary>
/// Stateless orchestrator for pre-tool structured-output inference.
/// Builds an <see cref="InferenceCompletionRequest"/>, calls the port, and forwards the
/// resulting JSON to <see cref="TaskInferenceStep"/> for confidence-based merge.
/// Idempotency (once-per-turn) is the caller's responsibility — see 16.3's InferenceTriggerFilter.
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
        ChatHistory history,
        string functionName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new InferenceCompletionRequest(history, strategy, functionName, arguments);
            var json = await _port.CompleteStructuredAsync(request, cancellationToken).ConfigureAwait(false);
            return await _mergeStep.ExecuteAsync(json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TaskInferenceRunner: inference failed for {FunctionName}; returning empty result", functionName);
            return new TaskInferenceResult(
                TotalFieldsInSchema: strategy.Fields.Count,
                FieldsInLlmResponse: 0,
                MergedFields: new Dictionary<string, TaskInferenceMergeOutcome>());
        }
    }
}
