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
    private readonly TimeProvider _time;

    /// <param name="contextFabric">The conversation's own field state.</param>
    /// <param name="logger">Merge diagnostics.</param>
    /// <param name="timeProvider">
    /// The clock every tag this step mints is stamped with. A tag says when the claim it carries was
    /// made — the v0.1 tag requires it — and there is one clock in this framework, injected, so a
    /// fixture that pins an instant sees the instant it pinned.
    /// </param>
    public TaskInferenceStep(
        ContextFabric contextFabric,
        ILogger<TaskInferenceStep> logger,
        TimeProvider? timeProvider = null)
    {
        _contextFabric = contextFabric ?? throw new ArgumentNullException(nameof(contextFabric));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
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
    ///
    /// <para>
    /// This overload has no turn in hand, so the port's <c>presence</c> and <c>utteranceSpan</c>
    /// stand as reported. Every shipped bridge reaches the step through
    /// <see cref="Services.TaskInferenceRunner"/>, which has the history and passes the utterance to
    /// the overload that verifies (PV-3).
    /// </para>
    /// </summary>
    // RS0027 wants the overload carrying optional parameters to be the longest one. Here the
    // shipped shape is the shorter one, and it keeps its optional token: the utterance overload is
    // additive, and taking this default away would break every caller compiled against beta.3.
#pragma warning disable RS0027
    public Task<TaskInferenceResult> ExecuteAsync(
        ITaskInferenceStrategy strategy,
        JsonElement llmStructuredOutput,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(strategy, llmStructuredOutput, utterance: null, cancellationToken);
#pragma warning restore RS0027

    /// <summary>
    /// The same merge, with the turn the values are graded against.
    /// </summary>
    /// <param name="strategy">The field schema the JSON is read through.</param>
    /// <param name="llmStructuredOutput">What the inference port reported, per field.</param>
    /// <param name="utterance">
    /// The current turn's user text, unmodified, or <see langword="null"/> when the caller has no
    /// turn. With a turn, presence is established from it and the port's claim is only a hint; with
    /// <see langword="null"/>, the port's <c>presence</c> and <c>utteranceSpan</c> stand as reported.
    /// </param>
    /// <param name="cancellationToken">Cancels the merge between fields.</param>
    public Task<TaskInferenceResult> ExecuteAsync(
        ITaskInferenceStrategy strategy,
        JsonElement llmStructuredOutput,
        string? utterance,
        CancellationToken cancellationToken)
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
            // PV-3's condition is "the value is literally present in the utterance", which is a
            // property of two strings. With the turn in hand the framework looks: a hit is
            // Conversation, bound to the place it was found, and anything else is Inferred. The
            // port's `presence` and `utteranceSpan` are hints — a span is used when the utterance at
            // that span says what the port said it says, a claimed `literal` the text does not
            // confirm is Inferred, and a value the port said nothing about is Conversation when it
            // is there to read. With no turn (no shipped caller: the runner always has the history)
            // the port's report is all there is, and stands.
            var (presence, binding) = Grade(fieldEl, newText, utterance);

            var candidateTag = ProvenanceTag.FromInference(
                presence, field.Name, newConfidence, binding, _time.GetUtcNow());
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
    /// The grade PV-3 gives one field, and the <c>utterance-span</c> binding that goes with it
    /// (PV-2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// With a turn in hand the framework establishes presence itself, by
    /// <see cref="UtterancePresence"/>: a hit is <see cref="InferenceSource.Conversation"/> bound to
    /// the span it was found at, with the digest taken over the utterance's own bytes — what was
    /// there when it was read. No hit is <see cref="InferenceSource.Inferred"/> and no binding: a tag
    /// with no binding is a weaker claim, not a false one, and inventing a span would be the false
    /// one.
    /// </para>
    /// <para>
    /// With no turn, the port's <c>presence</c> and <c>utteranceSpan</c> are all there is and stand
    /// as reported; the digest is then over the value the port reported, which is what it says the
    /// span contained.
    /// </para>
    /// </remarks>
    private static (InferenceSource Presence, ProvenanceBinding? Binding) Grade(
        JsonElement fieldEl, string valueText, string? utterance)
    {
        var (hintedOffset, hintedLength) = SpanHintOf(fieldEl, valueText);

        if (utterance is null)
        {
            var presence =
                fieldEl.TryGetProperty("presence", out var presenceEl)
                && string.Equals(presenceEl.GetString(), "literal", StringComparison.OrdinalIgnoreCase)
                    ? InferenceSource.Conversation
                    : InferenceSource.Inferred;

            // The digest is the canonical form's own — SHA-256 as 64 lowercase hexadecimal
            // characters, and nothing else — because a second implementation checking this span has
            // to produce the same string from the same bytes (SR-1, PV-2).
            var reported = hintedOffset is { } start && hintedLength is { } stated
                ? new ProvenanceBinding.UtteranceSpan(new UtteranceSpanRef(
                    start,
                    stated,
                    Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(valueText)))))
                : null;

            return (presence, reported);
        }

        return UtterancePresence.Locate(utterance, valueText, hintedOffset, hintedLength) is { } span
            ? (InferenceSource.Conversation,
                new ProvenanceBinding.UtteranceSpan(new UtteranceSpanRef(
                    span.Offset, span.Length, UtterancePresence.DigestOf(utterance, span))))
            : (InferenceSource.Inferred, null);
    }

    /// <summary>
    /// The offsets the port reported, if it reported any: <c>start</c> with either <c>end</c> or
    /// <c>length</c>, and <paramref name="valueText"/>'s own length when it named neither.
    /// </summary>
    private static (int? Offset, int? Length) SpanHintOf(JsonElement fieldEl, string valueText)
    {
        if (!fieldEl.TryGetProperty("utteranceSpan", out var span)
            || span.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        if (!span.TryGetProperty("start", out var startEl) || !startEl.TryGetInt32(out var start))
            return (null, null);

        var length =
            span.TryGetProperty("end", out var endEl) && endEl.TryGetInt32(out var end) ? end - start
            : span.TryGetProperty("length", out var lengthEl) && lengthEl.TryGetInt32(out var stated) ? stated
            : valueText.Length;

        return (start, length);
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

    /// <summary>
    /// The same value as text — the string the finder looks for in the utterance, and what a span's
    /// digest is taken over when there is no turn to take it over instead.
    /// </summary>
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
