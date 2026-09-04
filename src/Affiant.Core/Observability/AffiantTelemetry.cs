using System.Diagnostics;
using System.Diagnostics.Metrics;
using Affiant.Abstractions.Telemetry;

namespace Affiant.Core.Observability;

/// <summary>
/// Process-global telemetry surface for the Affiant framework.
/// Exposes the canonical ActivitySource, Meter, and pre-defined instruments
/// that all observability components write against.
///
/// <para>
/// The <c>Record*</c> methods below emit the events named in
/// <see cref="Affiant.Abstractions.Telemetry.TelemetryKeys"/> — the versioned registry the protocol
/// rulebook's rule TL-1 requires. They are the only supported way to emit a registry event: every
/// name and attribute they write is a constant on that class, so an event name that is not in the
/// registry cannot be spelled at a call site, and <c>TelemetryKeysTests</c> asserts the emitted set
/// stays inside the registry.
/// </para>
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

    // ── The telemetry-key registry (rulebook rules TL-1, TL-2) ───────────────────────────────
    //
    // Every method below emits exactly one registry event onto the ambient Affiant activity.
    // Attributes whose value this release cannot know are passed as null and are dropped here, so
    // they are absent from the event rather than guessed: an operator reading `docket.transition`
    // should see no `attestation.kind` at all until the row carries one, not a null that looks like
    // data the gate went and fetched. (ActivityTagsCollection keeps a null added through Add — only
    // its indexer removes on null — so the filtering has to be explicit.)

    private static void EmitRegistryEvent(string key, params (string Name, object? Value)[] attributes)
    {
        var target = FindAffiantActivity() ?? Activity.Current;
        if (target is null) return;

        var tags = new ActivityTagsCollection();
        foreach (var (name, value) in attributes)
        {
            if (value is not null) tags.Add(name, value);
        }

        target.AddEvent(new ActivityEvent(key, tags: tags));
    }

    /// <summary>
    /// Emits <see cref="TelemetryKeys.AffidavitFiled"/>: an Affidavit became a Docket entry.
    /// </summary>
    /// <param name="toolName">The tool that proposed the write.</param>
    /// <param name="conversationId">The conversation the proposal was made in.</param>
    /// <param name="entryId">The filed entry.</param>
    /// <param name="status">The entry's review status at filing.</param>
    /// <param name="fieldCount">How many fields the Affidavit swears to — a count, never the fields.</param>
    /// <param name="created"><see langword="true"/> when this call filed the entry, <see langword="false"/> on an idempotent replay.</param>
    /// <param name="requirement">
    /// The requirement level the policy chain returned, or <see langword="null"/> where it is not yet
    /// known. In this release the .NET gate files before it evaluates the policy chain, so filing
    /// time never knows the requirement and the attribute is absent; it arrives when the pipeline is
    /// reordered to the rulebook's GT-1 order.
    /// </param>
    public static void RecordAffidavitFiled(
        string toolName,
        string? conversationId,
        Guid entryId,
        string status,
        int fieldCount,
        bool created,
        string? requirement = null)
        => EmitRegistryEvent(
            TelemetryKeys.AffidavitFiled,
            (TelemetryKeys.Attributes.GenAiToolName, toolName),
            (TelemetryKeys.Attributes.GenAiConversationId, conversationId),
            (TelemetryKeys.Attributes.EntryId, entryId.ToString()),
            (TelemetryKeys.Attributes.DocketRequirement, requirement),
            (TelemetryKeys.Attributes.DocketStatus, status),
            (TelemetryKeys.Attributes.AffidavitFieldCount, fieldCount),
            (TelemetryKeys.Attributes.Created, created));

    /// <summary>
    /// Emits <see cref="TelemetryKeys.AffidavitRefusedSubstance"/>: a proposal swears to nothing (GT-3).
    /// </summary>
    /// <param name="toolName">The tool that proposed the write, where the emitting seam knows it.</param>
    /// <param name="conversationId">The conversation the proposal was made in.</param>
    /// <param name="fieldCount">How many fields the Affidavit carries.</param>
    /// <param name="reason">Which half of the substance rule was broken, as a stable code.</param>
    public static void RecordSubstanceRefused(
        string? toolName,
        string? conversationId,
        int fieldCount,
        string reason)
        => EmitRegistryEvent(
            TelemetryKeys.AffidavitRefusedSubstance,
            (TelemetryKeys.Attributes.GenAiToolName, toolName),
            (TelemetryKeys.Attributes.GenAiConversationId, conversationId),
            (TelemetryKeys.Attributes.AffidavitFieldCount, fieldCount),
            (TelemetryKeys.Attributes.Reason, reason));

    /// <summary>
    /// Emits <see cref="TelemetryKeys.CoverageRefused"/>: the gate cannot see a tool it must cover (CV-4).
    /// </summary>
    /// <param name="toolName">The tool that cannot be intercepted.</param>
    /// <param name="category">The category of tool — hosted, provider-executed, unauditable.</param>
    /// <param name="phase"><c>wire-up</c> or <c>proposal</c>.</param>
    public static void RecordCoverageRefused(string toolName, string category, string phase)
        => EmitRegistryEvent(
            TelemetryKeys.CoverageRefused,
            (TelemetryKeys.Attributes.GenAiToolName, toolName),
            (TelemetryKeys.Attributes.CoverageCategory, category),
            (TelemetryKeys.Attributes.Phase, phase));

    /// <summary>
    /// Emits <see cref="TelemetryKeys.DocketTransition"/>: a Docket entry changed state (DK-1).
    /// Emit it only from the caller whose own guarded write affected the row — a transition another
    /// caller won is that caller's to report.
    /// </summary>
    /// <param name="entryId">The entry that transitioned.</param>
    /// <param name="conversationId">The conversation the entry belongs to.</param>
    /// <param name="from">The state the entry left.</param>
    /// <param name="to">The state the entry entered.</param>
    /// <param name="amended">Whether the transition carried reviewer amendments.</param>
    /// <param name="execution">
    /// The execution outcome on an approved row, or <see langword="null"/> where the row carries
    /// none. This release has no execution-outcome state, so it is always absent.
    /// </param>
    /// <param name="decisionKind">The kind of decision, where the seam knows it.</param>
    /// <param name="attestationKind">
    /// The kind of attestation written on the row, or <see langword="null"/>. This release writes no
    /// attestation record, so it is always absent.
    /// </param>
    public static void RecordDocketTransition(
        Guid entryId,
        string? conversationId,
        string from,
        string to,
        bool? amended = null,
        string? execution = null,
        string? decisionKind = null,
        string? attestationKind = null)
        => EmitRegistryEvent(
            TelemetryKeys.DocketTransition,
            (TelemetryKeys.Attributes.EntryId, entryId.ToString()),
            (TelemetryKeys.Attributes.GenAiConversationId, conversationId),
            (TelemetryKeys.Attributes.From, from),
            (TelemetryKeys.Attributes.To, to),
            (TelemetryKeys.Attributes.Execution, execution),
            (TelemetryKeys.Attributes.DecisionKind, decisionKind),
            (TelemetryKeys.Attributes.AttestationKind, attestationKind),
            (TelemetryKeys.Attributes.Amended, amended));

    /// <summary>
    /// Emits <see cref="TelemetryKeys.DocketExpired"/>: a pending entry passed its deadline (DK-3).
    /// </summary>
    /// <param name="entryId">The entry the sweep expired.</param>
    public static void RecordDocketExpired(Guid entryId)
        => EmitRegistryEvent(
            TelemetryKeys.DocketExpired,
            (TelemetryKeys.Attributes.EntryId, entryId.ToString()));

    /// <summary>
    /// Emits <see cref="TelemetryKeys.DecisionUnauthorized"/>: a decision was refused (AZ-2).
    /// </summary>
    /// <param name="entryId">The entry the decision named.</param>
    /// <param name="conversationId">The conversation the entry belongs to, where the seam knows it.</param>
    /// <param name="reason">
    /// The rulebook's refusal code — <c>entry-not-found</c>, <c>decision-not-pending</c>,
    /// <c>decision-expired</c>, <c>decision-lost-race</c>, and once the framework checks identity,
    /// <c>decision-unauthorized</c>.
    /// </param>
    /// <param name="path">Which path refused: <c>decide</c>, <c>mark-executed</c> or <c>resubmit</c>.</param>
    /// <param name="principalKind">
    /// The kind of principal that acted — the kind, never the identifier. This release's decision
    /// surface takes no principal, so it is absent until the gate checks identity.
    /// </param>
    public static void RecordDecisionUnauthorized(
        Guid entryId,
        string? conversationId,
        string reason,
        string path,
        string? principalKind = null)
        => EmitRegistryEvent(
            TelemetryKeys.DecisionUnauthorized,
            (TelemetryKeys.Attributes.EntryId, entryId.ToString()),
            (TelemetryKeys.Attributes.GenAiConversationId, conversationId),
            (TelemetryKeys.Attributes.Reason, reason),
            (TelemetryKeys.Attributes.PrincipalKind, principalKind),
            (TelemetryKeys.Attributes.Path, path));

    /// <summary>
    /// Emits <see cref="TelemetryKeys.StandingOrderFired"/>: a policy approved a write with no
    /// person present (AZ-1).
    /// </summary>
    /// <param name="policyId">The policy that fired.</param>
    /// <param name="riskScore">The score the host's risk function returned.</param>
    /// <param name="entryId">The entry the verdict applies to, where the seam knows it.</param>
    /// <param name="policyVersion">The policy's own version, where the policy declares one.</param>
    public static void RecordStandingOrderFired(
        string policyId,
        int? riskScore = null,
        Guid? entryId = null,
        string? policyVersion = null)
        => EmitRegistryEvent(
            TelemetryKeys.StandingOrderFired,
            (TelemetryKeys.Attributes.PolicyId, policyId),
            (TelemetryKeys.Attributes.PolicyVersion, policyVersion),
            (TelemetryKeys.Attributes.EntryId, entryId?.ToString()),
            (TelemetryKeys.Attributes.RiskScore, riskScore));

    /// <summary>
    /// Emits <see cref="TelemetryKeys.StandingOrderBlocked"/>: a Standing Order verdict was not
    /// honoured (GT-5, PV-4).
    /// </summary>
    /// <param name="policyId">The policy whose verdict was not honoured.</param>
    /// <param name="blockedReason">
    /// The stable code to alert on: <c>mandatory-field-empty</c>, <c>unbound-declared-input</c> or
    /// <c>risk-above-threshold</c>.
    /// </param>
    /// <param name="reason">The sentence a reviewer would read. Free to be rephrased; never alert on it.</param>
    /// <param name="riskScore">The score the host's risk function returned.</param>
    /// <param name="riskThreshold">The policy's declared threshold.</param>
    /// <param name="policyVersion">The policy's own version, where the policy declares one.</param>
    /// <param name="provenanceField">The field whose provenance blocked the verdict — the name, never the value.</param>
    /// <param name="provenanceSource">The provenance source that blocked the verdict.</param>
    /// <param name="emptyMandatoryFields">The mandatory fields that read Empty — names, never values.</param>
    public static void RecordStandingOrderBlocked(
        string policyId,
        string blockedReason,
        string reason,
        int? riskScore = null,
        int? riskThreshold = null,
        string? policyVersion = null,
        string? provenanceField = null,
        string? provenanceSource = null,
        string? emptyMandatoryFields = null)
        => EmitRegistryEvent(
            TelemetryKeys.StandingOrderBlocked,
            (TelemetryKeys.Attributes.PolicyId, policyId),
            (TelemetryKeys.Attributes.PolicyVersion, policyVersion),
            (TelemetryKeys.Attributes.BlockedReason, blockedReason),
            (TelemetryKeys.Attributes.Reason, reason),
            (TelemetryKeys.Attributes.ProvenanceField, provenanceField),
            (TelemetryKeys.Attributes.ProvenanceSource, provenanceSource),
            (TelemetryKeys.Attributes.AffidavitEmptyMandatoryFields, emptyMandatoryFields),
            (TelemetryKeys.Attributes.RiskScore, riskScore),
            (TelemetryKeys.Attributes.RiskThreshold, riskThreshold));

    /// <summary>
    /// Emits <see cref="TelemetryKeys.PolicyInvalid"/>: an approval policy or the gate's deadline
    /// configuration broke its own contract (GT-4, CV-1).
    /// </summary>
    /// <param name="policyId">The policy, or the configuration surface, that broke the contract.</param>
    /// <param name="option">Which half broke: an option's name, or <c>evaluate</c>.</param>
    /// <param name="reason">What was wrong, in one line.</param>
    /// <param name="policyVersion">The policy's own version, where the policy declares one.</param>
    public static void RecordPolicyInvalid(
        string policyId,
        string option,
        string reason,
        string? policyVersion = null)
        => EmitRegistryEvent(
            TelemetryKeys.PolicyInvalid,
            (TelemetryKeys.Attributes.PolicyId, policyId),
            (TelemetryKeys.Attributes.PolicyVersion, policyVersion),
            (TelemetryKeys.Attributes.Option, option),
            (TelemetryKeys.Attributes.Reason, reason));
}

/// <summary>
/// Event names this framework emitted before the rulebook's telemetry-key registry existed, and
/// which a registry key now supersedes.
///
/// <para>
/// Each name here is still emitted, from the site that has always emitted it, for one release —
/// <c>1.0.0-beta.3</c> — so an operator's existing alert keeps firing while they move it. Each is
/// <see cref="ObsoleteAttribute"/>-marked with the registry key that replaces it. They are removed
/// in the release after <c>1.0.0-beta.3</c>; a name that is removed here is gone from the emitted
/// stream in the same release, which is why the deprecation window exists at all.
/// </para>
///
/// <para>
/// The framework's other event names — <c>affiant.tool_error</c>, <c>affiant.review.filing_failed</c>,
/// <c>affiant.review.broadcast_failed</c>, <c>affiant.extractor.failed</c>, and the
/// <c>inference.*</c> family — are <em>not</em> deprecated: they name things the registry does not
/// cover (a transport failure, a post-processing failure, the inference step's own progress) rather
/// than aliasing a registry key, and they keep their names.
/// </para>
/// </summary>
public static class DeprecatedTelemetryKeys
{
    /// <summary>
    /// The projection's per-Affidavit summary event. Superseded by
    /// <see cref="TelemetryKeys.AffidavitFiled"/>, which the gate emits when the Affidavit becomes a
    /// Docket entry and which carries the entry id, the tool, the conversation and the field count.
    /// The hollow-Affidavit half of what this event was used to detect is now
    /// <see cref="TelemetryKeys.AffidavitRefusedSubstance"/>, emitted from the same projection seam.
    /// </summary>
    [Obsolete(
        "Superseded by TelemetryKeys.AffidavitFiled (\"affidavit.filed\"), emitted by ReviewGate at " +
        "filing, and by TelemetryKeys.AffidavitRefusedSubstance (\"affidavit.refused.substance\") for " +
        "the hollow-Affidavit case. Still emitted through 1.0.0-beta.3; removed in the release after it.")]
    public const string AffidavitProjected = "affidavit.projected";
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
