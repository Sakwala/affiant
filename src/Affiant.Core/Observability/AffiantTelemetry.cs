using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Affiant.Core.Observability;

/// <summary>
/// Process-global telemetry surface for the Affiant framework.
/// Exposes the canonical ActivitySource, Meter, and pre-defined instruments
/// that all observability components write against.
/// </summary>
public static class AffiantTelemetry
{
    public static readonly ActivitySource AffiantActivitySource = new("Affiant.Framework");

    /// <summary>
    /// Separate ActivitySource for L2 inference events. Lets consumers subscribe to inference
    /// telemetry independently of every framework span. The Validator (Phase 3.5) subscribes
    /// only to this source; general-purpose pipelines subscribe to both.
    /// </summary>
    public static readonly ActivitySource AffiantTaskInferenceActivitySource = new("Affiant.TaskInference");

    public static readonly Meter AffiantMeter = new("Affiant.Framework");

    // Histograms — duration measurements in milliseconds
    public static readonly Histogram<double> TurnDuration =
        AffiantMeter.CreateHistogram<double>("affiant.turn.duration", unit: "ms");

    public static readonly Histogram<double> ReviewWaitDuration =
        AffiantMeter.CreateHistogram<double>("affiant.review.wait_duration", unit: "ms");

    // Counters — tagged event counts
    // Tags: purpose ∈ { "orchestration", "inference" }
    public static readonly Counter<long> TokenUsage =
        AffiantMeter.CreateCounter<long>("affiant.token.usage");

    // Tags: result ∈ { "approved", "rejected", "expired", "standing_order" }
    public static readonly Counter<long> ReviewOutcome =
        AffiantMeter.CreateCounter<long>("affiant.review.outcome");

    // Tags: reason ∈ { "primary_failure", "both_failed" }
    public static readonly Counter<long> ProviderDegraded =
        AffiantMeter.CreateCounter<long>("affiant.provider.degraded");

    /// <summary>
    /// Walks up from <see cref="Activity.Current"/> to the nearest ancestor whose
    /// <see cref="Activity.Source"/> is <see cref="AffiantActivitySource"/> ("Affiant.Framework").
    /// Framework-owned span events (e.g. <c>affiant.tool_error</c>, <c>affiant.review.filing_failed</c>)
    /// should record on this ambient Affiant activity rather than whatever backend (SK/MAF) activity
    /// happens to be current, so the event lands on the framework's own trace regardless of how many
    /// backend-specific spans are nested around the tool call at the point of failure. Extracted from
    /// <c>ToolErrorFilter</c> (P1a) so completion-stage emitters (<c>ReviewGateFilter</c>) share the
    /// same walk-up logic instead of duplicating it.
    /// </summary>
    public static Activity? FindAffiantActivity()
    {
        var current = Activity.Current;
        while (current is not null)
        {
            if (current.Source.Name == AffiantActivitySource.Name) return current;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Emits the <c>affiant.extractor.failed</c> span event (area-3 P2 ruling 3 — gate ruling
    /// "extractor policy = surface-and-continue"): a post-tool filter/extractor (a
    /// <c>ContextExtractor</c> subclass, <c>TaskInferenceMergeFilter</c>, or — as a generic backstop
    /// — anything <c>ToolErrorFilter</c> catches after <c>ToolInvocationContext.ToolExecuted</c> is
    /// already <see langword="true"/>) threw after the tool already produced a genuine result. The
    /// tool's result is never touched by this event's emission — it is purely an operator-visible
    /// record that post-processing lost a fact, not a signal to the model or a retry trigger.
    /// </summary>
    /// <param name="extractorType">
    /// The concrete post-tool filter type that failed (e.g. <c>GetType().Name</c> of the
    /// <c>ContextExtractor</c> subclass), or a fixed sentinel when the specific filter is not known
    /// (the generic <c>ToolErrorFilter</c> backstop path — see its remarks).
    /// </param>
    /// <param name="toolName">The tool call the failed post-processing was attached to.</param>
    /// <param name="ex">The caught exception — never rethrown by the caller.</param>
    public static void RecordExtractorFailedEvent(string extractorType, string toolName, Exception ex)
    {
        var target = FindAffiantActivity() ?? Activity.Current;
        target?.AddEvent(new ActivityEvent("affiant.extractor.failed",
            tags: new ActivityTagsCollection
            {
                { "extractor.type", extractorType },
                { "tool.name", toolName },
                { "exception.type", ex.GetType().Name },
            }));
    }
}

/// <summary>
/// Canonical OTel attribute-key constants for L2 inference telemetry.
/// All 12 keys are the public observability API at v1.0.0 — any rename requires a v2.0.0 major.
/// Centralised here so typos are findable in one place rather than scattered across emitters.
/// Consumed by Affiant.Core emitters and Affiant.SemanticKernel adapter emitters.
/// </summary>
public static class L2TelemetryKeys
{
    public const string FunctionName = "affiant.function.name";
    public const string PluginName = "affiant.plugin.name";
    public const string EntityType = "affiant.entity.type";
    public const string StrategyType = "affiant.strategy.type";
    public const string FieldsMerged = "affiant.fields.merged";
    public const string FieldsInResponse = "affiant.fields.in_response";
    public const string FieldsInSchema = "affiant.fields.in_schema";
    public const string SkipReason = "affiant.skip.reason";
    public const string ErrorKind = "affiant.error.kind";
    public const string AffidavitPopulatedFieldCount = "affiant.affidavit.populated_field_count";
    public const string AffidavitAggregateConfidence = "affiant.affidavit.aggregate_confidence";
    public const string AffidavitEmptyProvenanceFieldCount = "affiant.affidavit.empty_provenance_field_count";
}
