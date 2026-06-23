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
/// The strategy is accepted as a parameter to ExecuteAsync (not a constructor dependency),
/// enabling multi-write hosts where each write tool uses its own strategy without a
/// single-strategy DI fallback binding.
///
/// This class has no SK dependency and is testable without a kernel.
/// </summary>
public sealed class TaskInferenceStep
{
    private readonly ContextFabric _contextFabric;
    private readonly ILogger<TaskInferenceStep> _logger;

    public TaskInferenceStep(
        ContextFabric contextFabric,
        ILogger<TaskInferenceStep> logger)
    {
        _contextFabric = contextFabric ?? throw new ArgumentNullException(nameof(contextFabric));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Merges the LLM's structured-output response into the ContextFabric using the
    /// provided strategy's field schema. The strategy is passed per-invocation so
    /// multi-write hosts can route each tool call to its own strategy without a
    /// singleton DI binding.
    ///
    /// The JSON element must be an object where each property matches a field name from
    /// <paramref name="strategy"/>.Fields, with "value" (any JSON scalar — string, number, or
    /// boolean) and "confidence" (float or string) sub-properties. Fields absent from the JSON,
    /// carrying a non-scalar value, or below the threshold are skipped.
    /// </summary>
    public Task<TaskInferenceResult> ExecuteAsync(
        ITaskInferenceStrategy strategy,
        JsonElement llmStructuredOutput,
        CancellationToken cancellationToken = default)
    {
        var mergedFields = new Dictionary<string, TaskInferenceMergeOutcome>();
        var winningValues = new Dictionary<string, object>();

        foreach (var field in strategy.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!llmStructuredOutput.TryGetProperty(field.Name, out var fieldEl))
                continue;

            if (!fieldEl.TryGetProperty("value", out var valueEl) ||
                !fieldEl.TryGetProperty("confidence", out var confEl))
                continue;

            var newValue = ReadScalarValue(valueEl);
            if (string.IsNullOrEmpty(newValue))
                continue;

            float newConfidence;
            if (confEl.ValueKind == JsonValueKind.Number)
                newConfidence = confEl.GetSingle();
            else if (!float.TryParse(confEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out newConfidence))
                continue;

            if (strategy.MinimumConfidenceThreshold.HasValue &&
                newConfidence < (float)strategy.MinimumConfidenceThreshold.Value)
            {
                mergedFields[field.Name] = new TaskInferenceMergeOutcome(field.Name, false,
                    $"Confidence {newConfidence} below threshold {strategy.MinimumConfidenceThreshold}");
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
            var existing = _contextFabric.GetByKey(strategy.EntityName);
            var fields = existing != null
                ? new Dictionary<string, object>(existing.Fields)
                : new Dictionary<string, object>();
            foreach (var (k, v) in winningValues)
                fields[k] = v;

            _contextFabric.Upsert(new EntityRef(
                EntityType: strategy.EntityName,
                EntityId: strategy.EntityName,
                DisplayName: $"Inferred {strategy.EntityName}",
                Fields: fields));

            _logger.LogDebug(
                "TaskInferenceStep merged {WinCount} field(s) into {EntityName}",
                winningValues.Count, strategy.EntityName);
        }

        return Task.FromResult(new TaskInferenceResult(
            TotalFieldsInSchema: strategy.Fields.Count,
            FieldsInLlmResponse: llmStructuredOutput.EnumerateObject().Count(),
            MergedFields: mergedFields));
    }

    /// <summary>
    /// Reads a field's "value" as a string regardless of the JSON scalar kind the LLM emitted.
    /// Structured-output models frequently return numeric or boolean fields as native JSON
    /// numbers/booleans (e.g. <c>"EstimatedHours": { "value": 4 }</c>) rather than strings, so
    /// calling <see cref="JsonElement.GetString"/> unconditionally throws and aborts the whole
    /// merge. Non-scalar kinds (object, array, null) return null and the field is skipped.
    /// </summary>
    private static string? ReadScalarValue(JsonElement valueEl) => valueEl.ValueKind switch
    {
        JsonValueKind.String => valueEl.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => valueEl.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// Returns the winning tag between <paramref name="a"/> and <paramref name="b"/>
    /// using the framework spec §2.3 merge rule: higher confidence wins;
    /// ties break by <see cref="ProvenanceSource"/> ordinal (lower = more deterministic).
    /// </summary>
    public static ProvenanceTag ResolveByConfidence(ProvenanceTag a, ProvenanceTag b)
    {
        var bWins =
            b.Confidence > a.Confidence ||
            (b.Confidence == a.Confidence && (int)b.Source < (int)a.Source);
        return bWins ? b : a;
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
