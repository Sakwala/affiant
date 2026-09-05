namespace Affiant.Core.Services;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IAffidavitProjection"/> driven by <see cref="ITaskInferenceStrategy.Fields"/>.
///
/// THE SHAPE IT SWEARS TO. The field list covers the strategy's declared <c>Projected</c> fields
/// exactly — every one present, no other present, asserted rather than assumed — with a proposed
/// field whose provenance is unknown present and tagged <see cref="ProvenanceTag.Empty"/> at
/// confidence 0 rather than quietly omitted. The entity id is non-null if and only if the operation
/// is update-shaped (<see cref="Operation.IsUpdateShaped"/>), and on an update every field's
/// <see cref="AffidavitField.PreviousValue"/> comes from the host's
/// <see cref="IPreviousValueSource"/> — consulted for updates only. The three confidence numbers
/// come from <see cref="AffidavitConfidence.Compute"/>: the aggregate is the MINIMUM over every
/// proposed field with an <c>Empty</c> field counting as 0, not a mean over the populated ones,
/// which let a nine-tenths-blank Affidavit report a perfect score.
///
/// Per-<c>Projected</c>-field resolution order (highest precedence first):
/// <see cref="IFieldResolver"/> → legacy <see cref="IDeterministicFieldSource"/> (obsolete but
/// kept working) → ContextFabric chain → <see cref="ProvenanceTag.Empty"/> (Rule 7).
/// <c>Projected == false</c> fields (extraction fields) never reach this ladder — they are
/// excluded from <c>Affidavit.Fields</c> entirely and instead collected into
/// <see cref="ExtractionFacts"/>, exposed only to <see cref="IFieldResolver"/> implementations.
///
/// CHAIN SEMANTICS (fixes the historical chain-truncation defect, "V4"): both the resolver and
/// legacy-source paths used to call <see cref="ProvenanceChain.From"/> unconditionally when a
/// deterministic value won, silently discarding whatever chain already existed for that field.
/// This projection now follows <c>TaskInferenceStep.ExecuteAsync</c>'s merge idiom instead: a
/// resolver/legacy tag is merged onto the prior chain via <see cref="ProvenanceChain.Merge"/> (or
/// <see cref="ProvenanceChain.From"/> only when no prior chain exists — structurally identical to
/// the old behavior in that case), so genuine conversation history is preserved in
/// <see cref="ProvenanceChain.Prior"/> rather than silently dropped.
/// </summary>
public sealed class SchemaDrivenAffidavitProjection : IAffidavitProjection
{
    private readonly ITaskInferenceStrategy _strategy;
    private readonly IEnumerable<IFieldResolver> _resolvers;
#pragma warning disable CS0618 // IDeterministicFieldSource is obsolete but kept fully functional — see type XML docs.
    private readonly IEnumerable<IDeterministicFieldSource> _deterministicSources;
#pragma warning restore CS0618
    private readonly ILogger<SchemaDrivenAffidavitProjection> _logger;
    private readonly IObservabilityEventStream<AffidavitEmittedEvent> _eventStream;
    private readonly IEnumerable<IPreviousValueSource> _previousValueSources;

    public SchemaDrivenAffidavitProjection(
        ITaskInferenceStrategy strategy,
        IEnumerable<IFieldResolver> resolvers,
#pragma warning disable CS0618 // IDeterministicFieldSource is obsolete but kept fully functional — see type XML docs.
        IEnumerable<IDeterministicFieldSource> deterministicSources,
#pragma warning restore CS0618
        ILogger<SchemaDrivenAffidavitProjection> logger,
        IObservabilityEventStream<AffidavitEmittedEvent> eventStream,
        IEnumerable<IPreviousValueSource>? previousValueSources = null)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _resolvers = resolvers ?? throw new ArgumentNullException(nameof(resolvers));
        _deterministicSources = deterministicSources ?? throw new ArgumentNullException(nameof(deterministicSources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventStream = eventStream ?? throw new ArgumentNullException(nameof(eventStream));
        _previousValueSources = previousValueSources ?? Array.Empty<IPreviousValueSource>();

        ValidateFieldSchema(_strategy);
    }

    public string EntityType => _strategy.EntityName;

    public Affidavit Project(
        IContextFabric fabric,
        string operationType,
        IReadOnlyList<string> warnings,
        string? entityId = null)
    {
        ArgumentNullException.ThrowIfNull(fabric);
        ArgumentNullException.ThrowIfNull(warnings);

        // AF-3's biconditional, checked before anything else is built: entityId non-null if and
        // only if the operation is update-shaped. Refusing here rather than guessing is the whole
        // point — a create-shaped Affidavit filed for an update is the defect this closes, and it
        // is invisible downstream because a null entity id reads exactly like a create.
        var isUpdate = Operation.IsUpdateShaped(operationType);
        if (isUpdate && string.IsNullOrEmpty(entityId))
        {
            throw new ArgumentException(
                $"Operation '{operationType}' is update-shaped, so the Affidavit must name the entity " +
                "it updates: pass entityId to Project(...). An update-shaped Affidavit with a null " +
                "entity id is indistinguishable from a create, and its fields would swear to no " +
                "previous values.",
                nameof(entityId));
        }

        if (!isUpdate && !string.IsNullOrEmpty(entityId))
        {
            throw new ArgumentException(
                $"Operation '{operationType}' is create-shaped, so the Affidavit names no entity: " +
                $"entityId must be null, not '{entityId}'. A non-null entity id is the protocol's " +
                "predicate for \"this is an update\", and a policy that tests it would be misled.",
                nameof(entityId));
        }

        // AF-3: the stored values an update replaces come from the host's own system of record,
        // through IPreviousValueSource. Consulted for updates only — a create has nothing to
        // replace — and null on a create and on any update field the entity had no value for.
        var previousValues = isUpdate
            ? ResolvePreviousValues(entityId!)
            : null;

        var entity = fabric.GetByKey(_strategy.EntityName);

        // Extraction facts (Projected == false fields) are computed once, up front, so
        // IFieldResolver implementations can see the full set for this projection.
        var facts = BuildExtractionFacts(fabric, entity);
        var resolverContext = new FieldResolutionContext(fabric, facts);

        // Fields are emitted in strategy.Fields declared order for deterministic reviewer UX.
        // Projected == false fields never become an AffidavitField — see BuildExtractionFacts.
        var fields = _strategy.Fields
            .Where(field => field.Projected)
            .Select(field =>
            {
                ProvenanceChain provenance;
                object? value = null;

                var resolution = TryResolve(field, resolverContext);
                if (resolution is not null)
                {
                    var (mergedChain, candidateWins) = MergeCandidateTag(fabric, field.Name, resolution.Tag);
                    provenance = mergedChain;
                    value = candidateWins ? resolution.Value : ExtractEntityValue(entity, field.Name, mergedChain.Current);
                }
                else
                {
                    // Legacy deterministic source (spec §1.4 / PRD §2.4): first non-null Resolve result
                    // takes precedence over the raw fabric chain — kept working, obsolete only.
#pragma warning disable CS0618 // IDeterministicFieldSource is obsolete but kept fully functional — see type XML docs.
                    var deterministicTag = _deterministicSources
                        .Where(s => s.FieldName == field.Name)
                        .Select(s => s.Resolve(fabric))
                        .FirstOrDefault(t => t is not null);
#pragma warning restore CS0618

                    if (deterministicTag is not null)
                    {
                        var (mergedChain, _) = MergeCandidateTag(fabric, field.Name, deterministicTag);
                        provenance = mergedChain;
                        value = ExtractEntityValue(entity, field.Name, mergedChain.Current);
                    }
                    else
                    {
                        var chain = fabric.GetFieldChain(field.Name);
                        if (chain is not null)
                        {
                            provenance = chain;

                            // The value as it stands, whatever the tag says about it — an Empty tag
                            // included. A field that asserts a value while swearing nothing about
                            // where it came from is GT-3's HOLLOW signature, and the gate refuses it
                            // by name; a projection that dropped the value here would turn every
                            // hollow proposal into an empty one and the refusal would report the
                            // wrong thing about it. What makes the claim safe is that the tag
                            // travels with the value and says the value has nothing behind it.
                            if (entity is not null && entity.Fields.TryGetValue(field.Name, out var fv))
                            {
                                value = fv;
                            }
                        }
                        else
                        {
                            // AF-1: never omit a field. A field with no chain has nothing behind it
                            // and is sworn Empty at confidence 0 — present, and honest about knowing
                            // nothing, which is what makes the aggregate 0 and the empty-field count
                            // include it. It also carries no value: a value with no provenance is
                            // exactly the claim the tag denies.
                            provenance = ProvenanceChain.From(ProvenanceTag.Empty);
                            value = null;
                        }
                    }
                }

                var (kind, allowedValues) = ClassifyKind(field);

                object? previousValue = null;
                previousValues?.TryGetValue(field.Name, out previousValue);

                return new AffidavitField(
                    field.Name, value, previousValue, provenance, field.Required,
                    Kind: kind, AllowedValues: allowedValues, Pattern: field.Pattern);
            })
            .ToArray();

        AssertExactCoverage(fields);

        // AF-2: the aggregate is the MINIMUM over every proposed field with an Empty field counting
        // as 0 — not the mean over the non-Empty ones, which let a nine-tenths-blank Affidavit
        // report a perfect score. The two companions are what make a 0 readable: how many fields
        // are blank, and how good the populated ones are.
        var confidence = AffidavitConfidence.Compute(fields);

        var affidavit = new Affidavit(
            operationType,
            _strategy.EntityName,
            EntityId: entityId,
            fields,
            AggregateConfidence: confidence.AggregateConfidence,
            PopulatedConfidence: confidence.PopulatedConfidence,
            EmptyFieldCount: confidence.EmptyFieldCount,
            Warnings: warnings.ToArray(),
            RequiresConfirmation: true);

        // Compute summary metrics for telemetry.
        var populatedFieldCount = affidavit.Fields.Count(
            f => f.Value is not null && (f.Value is not string s || !string.IsNullOrEmpty(s)));
        var emptyProvenanceFieldCount = affidavit.EmptyFieldCount;

        // Emit affidavit.projected span event with per-projection summary attributes.
        // DEPRECATED (1.0.0-beta.3): superseded by the registry's affidavit.filed, which ReviewGate
        // emits when this Affidavit becomes a Docket entry. Still emitted for one release so an
        // operator's existing alert keeps firing while they move it; removed in the release after.
#pragma warning disable CS0618 // deliberate: the deprecated alias is emitted alongside the registry key for one release.
        Activity.Current?.AddEvent(new ActivityEvent(
            DeprecatedTelemetryKeys.AffidavitProjected,
            tags: new ActivityTagsCollection
            {
                { L2TelemetryKeys.AffidavitPopulatedFieldCount, populatedFieldCount },
                { L2TelemetryKeys.AffidavitAggregateConfidence, affidavit.AggregateConfidence },
                { L2TelemetryKeys.AffidavitEmptyProvenanceFieldCount, emptyProvenanceFieldCount },
            }));
#pragma warning restore CS0618

        // Also set the summary tag on the current span for query-friendly lookup (PRD §6.3).
        // Activity.Current is typically a descendant of the invoke_agent root span;
        // OTel queries traverse the tree so this satisfies the root-span intent pragmatically.
        Activity.Current?.SetTag(L2TelemetryKeys.AffidavitPopulatedFieldCount, populatedFieldCount);

        // Publish typed event for Validator / host subscribers (PRD §6.4).
        // Design Note 2, Epic 17: ConversationId must come from a named field on the marker entity,
        // not the EntityId — the fabric keys entities by EntityId, so GetByKey("__conversation__")?.EntityId
        // would return the literal string "__conversation__" rather than the actual conversation id.
        // The host seeds this marker via RehydrateFabric in AgentRunner, storing the real id in Fields["ConversationId"].
        var conversationId = (fabric.GetByKey("__conversation__")?.Fields.GetValueOrDefault("ConversationId") as string)
            ?? Activity.Current?.GetBaggageItem("conversationId")
            ?? string.Empty;

        // TL-1 `affidavit.refused.substance` (GT-3). This release does not yet REFUSE a hollow
        // proposal at run time — the runtime refusal lands with the gate-pipeline change — so what
        // this event records today is the detection, on the same seam the refusal will be raised
        // from, with the same reason text. An operator can therefore build the alert now and see it
        // start refusing rather than start firing. The compliance harness's test-time check
        // (ComplianceHarness.AssertProvenanceIsSubstantive) uses the same three conditions.
        var substanceRefusal = AffidavitSubstance.DescribeFailure(affidavit);
        if (substanceRefusal is not null)
        {
            AffiantTelemetry.RecordSubstanceRefused(
                toolName: null,
                conversationId,
                affidavit.Fields.Length,
                substanceRefusal);
        }

        _eventStream.Publish(new AffidavitEmittedEvent(
            ConversationId: conversationId,
            // Not a protocol identity: this is the correlation id of one OBSERVABILITY event, read
            // by a host's own telemetry consumer and never written to a record, compared, or put on
            // a wire. The identities the rules are about — a Docket entry id (GT-4) — are derived.
            AffidavitId: Guid.NewGuid(),
            OperationType: operationType,
            EntityType: _strategy.EntityName,
            PopulatedFieldCount: populatedFieldCount,
            AggregateConfidence: affidavit.AggregateConfidence,
            EmptyProvenanceFieldCount: emptyProvenanceFieldCount));

        return affidavit;
    }

    /// <summary>
    /// Rejects a strategy declaring a field as both an extraction field (<c>Projected: false</c>)
    /// and mandatory (<c>Required: true</c>) — an extraction fact never becomes an
    /// <c>AffidavitField</c>, so it can never gate the Evidence Card. Runs once per projection
    /// construction so a misconfigured strategy fails loudly and immediately rather than silently
    /// producing a card that can never be blocked on a field the host believed was mandatory.
    /// </summary>
    private static void ValidateFieldSchema(ITaskInferenceStrategy strategy)
    {
        var invalidFieldNames = strategy.Fields
            .Where(f => !f.Projected && f.Required)
            .Select(f => f.Name)
            .ToArray();

        if (invalidFieldNames.Length == 0)
            return;

        throw new ArgumentException(
            $"Strategy '{strategy.GetType().Name}' declares field(s) [{string.Join(", ", invalidFieldNames)}] " +
            "with Projected=false and Required=true. An extraction fact (Projected=false) never becomes an " +
            "AffidavitField, so it cannot gate the Evidence Card. Set Required=false, or set Projected=true " +
            "if the field belongs on the card.",
            nameof(strategy));
    }

    /// <summary>
    /// Collects the extracted state of every <c>Projected == false</c> field into
    /// <see cref="ExtractionFacts"/>, reading directly from <c>fabric.GetFieldChain</c> — the raw
    /// extracted state, independent of the resolver/legacy-source ladder that only applies to
    /// <c>Projected == true</c> card fields. A field with no chain yet is simply absent from the
    /// result (may be absent) rather than present with a placeholder.
    /// </summary>
    private ExtractionFacts BuildExtractionFacts(IContextFabric fabric, EntityRef? entity)
    {
        var facts = new Dictionary<string, ExtractionFact>(StringComparer.Ordinal);

        foreach (var field in _strategy.Fields.Where(f => !f.Projected))
        {
            var chain = fabric.GetFieldChain(field.Name);
            if (chain is null)
                continue; // may be absent — no fabric state yet for this extraction field.

            object? value = null;
            if (chain.Current.Source != ProvenanceSource.Empty
                && entity is not null
                && entity.Fields.TryGetValue(field.Name, out var fv))
            {
                value = fv;
            }

            facts[field.Name] = new ExtractionFact(value, chain);
        }

        return new ExtractionFacts(facts);
    }

    private static object? ExtractEntityValue(EntityRef? entity, string fieldName, ProvenanceTag currentTag)
    {
        if (currentTag.Source == ProvenanceSource.Empty)
            return null;

        return entity is not null && entity.Fields.TryGetValue(fieldName, out var value) ? value : null;
    }

    /// <summary>
    /// Resolves <paramref name="field"/> via the first registered <see cref="IFieldResolver"/> for
    /// its name whose <see cref="IFieldResolver.ResolveAsync"/> returns non-null. Bridges the
    /// async resolver contract to this synchronous <see cref="IAffidavitProjection.Project"/>
    /// member (locked by <c>AffidavitProjectionInterfaceTests</c>) the same way
    /// <c>Affiant.Testing.ComplianceHarness.ComplianceHarness</c> bridges
    /// <c>TaskInferenceRunner.RunAsync</c> — safe in this framework's hosting model, which has no
    /// captured <see cref="SynchronizationContext"/> to deadlock against.
    /// </summary>
    private FieldResolution? TryResolve(TaskInferenceField field, FieldResolutionContext context)
    {
        foreach (var resolver in _resolvers.Where(r => r.FieldName == field.Name))
        {
            var resolution = resolver.ResolveAsync(context, CancellationToken.None).GetAwaiter().GetResult();
            if (resolution is not null)
                return resolution;
        }

        return null;
    }

    /// <summary>
    /// The V4 fix: merges <paramref name="candidate"/> (a resolver or legacy deterministic-source
    /// tag) onto whatever <see cref="ProvenanceChain"/> already exists for
    /// <paramref name="fieldName"/>, following <c>TaskInferenceStep.ExecuteAsync</c>'s merge idiom
    /// (higher confidence wins; ties break by <see cref="ProvenanceSource"/> ordinal) instead of
    /// unconditionally truncating history via <see cref="ProvenanceChain.From"/>. When no prior
    /// chain exists, the result is structurally identical to <c>ProvenanceChain.From(candidate)</c>.
    /// </summary>
    private static (ProvenanceChain Chain, bool CandidateWins) MergeCandidateTag(
        IContextFabric fabric, string fieldName, ProvenanceTag candidate)
    {
        var existingChain = fabric.GetFieldChain(fieldName);
        if (existingChain is null)
            return (ProvenanceChain.From(candidate), true);

        return (existingChain.Merge(candidate), candidate.Beats(existingChain.Current));
    }

    /// <summary>
    /// Asks each registered <see cref="IPreviousValueSource"/> in registration order for the stored
    /// values of <paramref name="entityId"/>, and returns the first non-null answer.
    ///
    /// <para>
    /// A source returning null means "I do not serve this entity type, ask the next one"; an empty
    /// map is a real answer ("I own it, and it holds nothing yet"). When no source answers, every
    /// previous value is null and the reviewer sees the same thing they saw before this port
    /// existed — which is why <c>AffiantWireUpValidator</c> refuses at startup for a host whose
    /// write tools declare update operations and registers no source at all.
    /// </para>
    ///
    /// <para>
    /// Bridges the async port to this synchronous <see cref="IAffidavitProjection.Project"/> member
    /// exactly the way <see cref="TryResolve"/> bridges <see cref="IFieldResolver"/> — safe in this
    /// framework's hosting model, which has no captured
    /// <see cref="System.Threading.SynchronizationContext"/> to deadlock against.
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<string, object?>? ResolvePreviousValues(string entityId)
    {
        foreach (var source in _previousValueSources)
        {
            var values = source
                .GetPreviousValuesAsync(_strategy.EntityName, entityId, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (values is not null)
                return values;
        }

        _logger.LogWarning(
            "SchemaDrivenAffidavitProjection: no IPreviousValueSource answered for {EntityType} " +
            "{EntityId}, so every field's PreviousValue on this update-shaped Affidavit is null — " +
            "a reviewer cannot see what is changing. Register a source with " +
            "services.AddPreviousValueSource<TSource>().",
            _strategy.EntityName, entityId);

        return null;
    }

    /// <summary>
    /// Asserts that the projected field list covers the strategy's declared projected fields
    /// exactly, in both directions: every declared field present, and no other field.
    ///
    /// <para>
    /// The loop above already builds the list from that same declaration, so this can only fire if
    /// something in the ladder starts adding or dropping rows. It is checked rather than assumed
    /// because the whole value of the field list is that it is a statement of intent a policy can
    /// read: a missing row silently narrows what the reviewer is asked to approve, and an extra one
    /// silently widens it — and neither is visible on the card.
    /// </para>
    /// </summary>
    private void AssertExactCoverage(IReadOnlyList<AffidavitField> fields)
    {
        var declared = _strategy.Fields.Where(f => f.Projected).Select(f => f.Name).ToArray();
        var projected = fields.Select(f => f.Name).ToArray();

        var missing = declared.Except(projected, StringComparer.Ordinal).ToArray();
        var extra = projected.Except(declared, StringComparer.Ordinal).ToArray();
        var duplicated = projected
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (missing.Length == 0 && extra.Length == 0 && duplicated.Length == 0)
            return;

        var problems = new List<string>();
        if (missing.Length > 0)
            problems.Add($"declared but not projected: [{string.Join(", ", missing)}]");
        if (extra.Length > 0)
            problems.Add($"projected but not declared: [{string.Join(", ", extra)}]");
        if (duplicated.Length > 0)
            problems.Add($"projected more than once: [{string.Join(", ", duplicated)}]");

        throw new InvalidOperationException(
            $"Strategy '{_strategy.GetType().Name}' projected an Affidavit whose fields do not cover " +
            $"its declared projected field set exactly — {string.Join("; ", problems)}. An Affidavit's " +
            "fields are exactly the fields the operation proposes: a proposed field whose provenance " +
            "is unknown is present and tagged Empty, and a field the operation does not propose is " +
            "absent.");
    }

    /// <summary>
    /// Derives <see cref="AffidavitField.Kind"/> (and, for enums, <see cref="AffidavitField.AllowedValues"/>)
    /// from a <see cref="TaskInferenceField"/>. Resolution order: an explicit
    /// <see cref="TaskInferenceField.Enum"/> wins over everything (Kind "enum"); otherwise a
    /// numeric <see cref="TaskInferenceField.JsonType"/> ("number" or "integer") maps to Kind
    /// "number"; otherwise an explicit <see cref="TaskInferenceField.Format"/> of "date" maps to
    /// Kind "date"; otherwise Kind defaults to "text". <see cref="TaskInferenceField.Pattern"/> is
    /// forwarded separately by the caller regardless of Kind.
    /// </summary>
    private static (string Kind, IReadOnlyList<string>? AllowedValues) ClassifyKind(TaskInferenceField field)
    {
        if (field.Enum is not null)
            return (AffidavitFieldKind.Enum, field.Enum);

        if (field.JsonType is "number" or "integer")
            return (AffidavitFieldKind.Number, null);

        if (field.Format == "date")
            return (AffidavitFieldKind.Date, null);

        return (AffidavitFieldKind.Text, null);
    }
}
