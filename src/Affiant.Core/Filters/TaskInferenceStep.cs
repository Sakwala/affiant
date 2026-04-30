namespace Affiant.Core.Filters;

using System.Globalization;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging;

/// <summary>
/// Domain-agnostic merge step for structured-output task inference.
/// Accepts a JSON element from the LLM representing inferred field values (each with a
/// "value" and "confidence"), applies the framework's confidence-based merge rule against
/// the ProvenanceChains stored in ContextFabric, and upserts winning values as an EntityRef.
///
/// Merge rule (framework spec §2.3): higher confidence wins; ties break by ProvenanceSource
/// ordinal (lower ordinal = more deterministic, e.g. UserStated=0 beats External=1).
///
/// This class has no SK dependency and is testable without a kernel.
/// </summary>
public sealed class TaskInferenceStep
{
    private readonly ITaskInferenceStrategy _strategy;
    private readonly ContextFabric _contextFabric;
    private readonly ILogger<TaskInferenceStep> _logger;

    public TaskInferenceStep(
        ITaskInferenceStrategy strategy,
        ContextFabric contextFabric,
        ILogger<TaskInferenceStep> logger)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _contextFabric = contextFabric ?? throw new ArgumentNullException(nameof(contextFabric));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Merges the LLM's structured-output response into the ContextFabric.
    /// The JSON element must be an object where each property matches a field name from
    /// ITaskInferenceStrategy.Fields, with "value" (string) and "confidence" (float or string)
    /// sub-properties. Fields absent from the JSON or below the threshold are skipped.
    /// </summary>
    public Task<TaskInferenceResult> ExecuteAsync(
        JsonElement llmStructuredOutput,
        CancellationToken cancellationToken = default)
    {
        var mergedFields = new Dictionary<string, TaskInferenceMergeOutcome>();
        var winningValues = new Dictionary<string, object>();

        foreach (var field in _strategy.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!llmStructuredOutput.TryGetProperty(field.Name, out var fieldEl))
                continue;

            if (!fieldEl.TryGetProperty("value", out var valueEl) ||
                !fieldEl.TryGetProperty("confidence", out var confEl))
                continue;

            var newValue = valueEl.GetString();
            if (string.IsNullOrEmpty(newValue))
                continue;

            float newConfidence;
            if (confEl.ValueKind == JsonValueKind.Number)
                newConfidence = confEl.GetSingle();
            else if (!float.TryParse(confEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out newConfidence))
                continue;

            if (_strategy.MinimumConfidenceThreshold.HasValue &&
                newConfidence < (float)_strategy.MinimumConfidenceThreshold.Value)
            {
                mergedFields[field.Name] = new TaskInferenceMergeOutcome(field.Name, false,
                    $"Confidence {newConfidence} below threshold {_strategy.MinimumConfidenceThreshold}");
                continue;
            }

            var candidateTag = ProvenanceTag.FromInference(field.Name, newConfidence);
            var currentChain = _contextFabric.GetFieldChain(field.Name);

            bool wins;
            string reason;
            if (currentChain == null)
            {
                wins = true;
                reason = "No existing value in fabric";
            }
            else
            {
                var current = currentChain.Current;
                wins = candidateTag.Confidence > current.Confidence ||
                       (candidateTag.Confidence == current.Confidence &&
                        (int)candidateTag.Source < (int)current.Source);
                reason = wins
                    ? $"Higher confidence: {candidateTag.Confidence} > {current.Confidence}"
                    : $"Lower or equal confidence: {candidateTag.Confidence} vs {current.Confidence}";
            }

            var updatedChain = currentChain == null
                ? ProvenanceChain.From(candidateTag)
                : currentChain.Merge(candidateTag);
            _contextFabric.SetFieldChain(field.Name, updatedChain);

            if (wins)
                winningValues[field.Name] = newValue;

            mergedFields[field.Name] = new TaskInferenceMergeOutcome(field.Name, wins, reason);
        }

        if (winningValues.Count > 0)
        {
            var existing = _contextFabric.GetByKey(_strategy.EntityName);
            var fields = existing != null
                ? new Dictionary<string, object>(existing.Fields)
                : new Dictionary<string, object>();
            foreach (var (k, v) in winningValues)
                fields[k] = v;

            _contextFabric.Upsert(new EntityRef(
                EntityType: _strategy.EntityName,
                EntityId: _strategy.EntityName,
                DisplayName: $"Inferred {_strategy.EntityName}",
                Fields: fields));

            _logger.LogDebug(
                "TaskInferenceStep merged {WinCount} field(s) into {EntityName}",
                winningValues.Count, _strategy.EntityName);
        }

        return Task.FromResult(new TaskInferenceResult(
            TotalFieldsInSchema: _strategy.Fields.Count,
            FieldsInLlmResponse: llmStructuredOutput.EnumerateObject().Count(),
            MergedFields: mergedFields));
    }
}

/// <summary>Summary of a TaskInferenceStep execution.</summary>
public record TaskInferenceResult(
    int TotalFieldsInSchema,
    int FieldsInLlmResponse,
    IReadOnlyDictionary<string, TaskInferenceMergeOutcome> MergedFields);

/// <summary>Outcome of attempting to merge a single inferred field.</summary>
public record TaskInferenceMergeOutcome(
    string FieldName,
    bool Merged,
    string Reason);
