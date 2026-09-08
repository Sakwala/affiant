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
/// </summary>
public sealed class TaskInferenceRunner
{
    private readonly IInferenceCompletionPort _port;
    private readonly IContextFabric _fabric;
    private readonly TaskInferenceStep _mergeStep;
    private readonly ILogger<TaskInferenceRunner> _logger;

    /// <summary>
    /// The role a person's turn carries in the neutral history. Every shipped bridge writes it:
    /// Semantic Kernel converts <c>AuthorRole.User.Label</c> and Extensions.AI (with the Agent
    /// Framework on top of it) converts <c>ChatRole.User.Value</c>, and both of those are "user".
    /// Matched case-insensitively because a host may build an <see cref="AffiantChatMessage"/> by
    /// hand.
    /// </summary>
    private const string UserRole = "user";

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

            // PV-3: the grade is a fact about the turn, so the step is given the turn. The utterance
            // is the last thing the person said in this history, unmodified — no earlier turn, since
            // an `utterance-span` binding names no message and a hit in an earlier turn could not be
            // bound to one.
            var result = await _mergeStep
                .ExecuteAsync(strategy, json, LatestUtterance(history), cancellationToken)
                .ConfigureAwait(false);

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
        catch (OperationCanceledException)
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

    /// <summary>
    /// The current turn's user text, or <see langword="null"/> when this history holds no turn of a
    /// person's — in which case the step has nothing to establish presence against and the port's
    /// own report stands.
    /// </summary>
    private static string? LatestUtterance(IReadOnlyList<AffiantChatMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (string.Equals(history[i].Role, UserRole, StringComparison.OrdinalIgnoreCase))
                return history[i].Content;
        }

        return null;
    }
}
