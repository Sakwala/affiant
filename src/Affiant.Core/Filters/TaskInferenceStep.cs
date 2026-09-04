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
/// ordinal (lower ordinal = more deterministic, e.g. UserStated=0 beats External=1). The comparison
/// itself is <see cref="ProvenanceTag.Beats"/>, so this step, the schema-driven projection and
/// <see cref="ProvenanceChain.Merge"/> cannot state the rule three slightly different ways.
///
/// A model-reported confidence is clamped into [0, 1] by <see cref="ProvenanceTag"/> itself, so a
/// model that answers 1.4 or -0.2 cannot mint a tag outside the range every other rule reads.
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

            // The value keeps the JSON type the port reported it as. A number reported as a number
            // is filed as a number: the field's `kind` is a rendering hint for a reviewer surface,
            // not a licence to re-type the value, and a card that showed "40" where the port said 40
            // would be showing a different value from the one the record swears to (AF-1, SR-2).
            var newValue = ReadScalarValue(valueEl);
            if (newValue is null)
                continue;

            // The text the span digest is taken over: what the port says was there to read.
            var newText = ReadScalarText(valueEl);
            if (string.IsNullOrEmpty(newText))
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

            // The inference step mints through ProvenanceTag.FromInference, whose source parameter
            // is an InferenceSource and therefore cannot name UserStated, External or Computed.
            // Those three are claims about an artifact outside the model's own reasoning — a
            // person's act, a system of record, a named rule — and an inference has none of them.
            // The restriction is structural, not a convention: there is no overload reachable from
            // here that could name them.
            // `presence` is the port's own answer to "was this value literally in the turn, or did
            // the model reason to it" (GT-1 step 3), and it is the difference between a Conversation
            // grade and an Inferred one. Absent, it is Inferred: a port that does not say has not
            // claimed the value was there to read.
            var presence =
                fieldEl.TryGetProperty("presence", out var presenceEl)
                && string.Equals(presenceEl.GetString(), "literal", StringComparison.OrdinalIgnoreCase)
                    ? InferenceSource.Conversation
                    : InferenceSource.Inferred;

            var candidateTag = ProvenanceTag.FromInference(
                presence, field.Name, newConfidence, UtteranceSpanOf(fieldEl, newText));
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
                wins = candidateTag.Beats(current);
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
    /// <summary>
    /// The span of the unmodified turn a value was read from, when the port named one (PV-2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Offset and length come from the port; the digest is over the value the port reported, which
    /// is what it says the span contained. A binding points at something an auditor can go and
    /// re-check, and this is the strongest such claim an inference can make: the turn is on the
    /// record, the offsets say where to look, and the digest says what was there when it was read.
    /// </para>
    /// <para>
    /// A port that names no span produces no binding: a tag with no binding is a weaker claim, not
    /// a false one, and inventing a span nobody reported would be the false one.
    /// </para>
    /// </remarks>
    private static ProvenanceBinding? UtteranceSpanOf(JsonElement fieldEl, string value)
    {
        if (!fieldEl.TryGetProperty("utteranceSpan", out var span)
            || span.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!span.TryGetProperty("start", out var startEl) || !startEl.TryGetInt32(out var start))
            return null;

        var length =
            span.TryGetProperty("end", out var endEl) && endEl.TryGetInt32(out var end) ? end - start
            : span.TryGetProperty("length", out var lengthEl) && lengthEl.TryGetInt32(out var stated) ? stated
            : value.Length;

        var digest = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

        return new ProvenanceBinding.UtteranceSpan(
            new UtteranceSpanRef(start, length, $"sha256:{digest}"));
    }

    /// <summary>
    /// A field's <c>value</c> as the JSON type the port reported, or <see langword="null"/> for a
    /// kind an Affidavit field cannot carry (object, array, JSON null).
    /// </summary>
    private static object? ReadScalarValue(JsonElement valueEl) => valueEl.ValueKind switch
    {
        JsonValueKind.String => valueEl.GetString() is { Length: > 0 } text ? text : null,
        // Boxed explicitly: a conditional whose arms are int and long has type long, so an int
        // would arrive on the card as a long and compare unequal to the number the port reported.
        JsonValueKind.Number => valueEl.TryGetInt64(out var whole)
            ? whole >= int.MinValue && whole <= int.MaxValue ? (object)(int)whole : whole
            : valueEl.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>The same value as text — what an utterance span's digest is taken over.</summary>
    private static string? ReadScalarText(JsonElement valueEl) => valueEl.ValueKind switch
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
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return b.Beats(a) ? b : a;
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
