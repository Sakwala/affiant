namespace Affiant.Core.Services;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IAffidavitProjection"/> driven by <see cref="ITaskInferenceStrategy.Fields"/>.
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

    public SchemaDrivenAffidavitProjection(
        ITaskInferenceStrategy strategy,
        IEnumerable<IFieldResolver> resolvers,
#pragma warning disable CS0618 // IDeterministicFieldSource is obsolete but kept fully functional — see type XML docs.
        IEnumerable<IDeterministicFieldSource> deterministicSources,
#pragma warning restore CS0618
        ILogger<SchemaDrivenAffidavitProjection> logger,
        IObservabilityEventStream<AffidavitEmittedEvent> eventStream)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _resolvers = resolvers ?? throw new ArgumentNullException(nameof(resolvers));
        _deterministicSources = deterministicSources ?? throw new ArgumentNullException(nameof(deterministicSources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventStream = eventStream ?? throw new ArgumentNullException(nameof(eventStream));

        ValidateFieldSchema(_strategy);
    }

    public string EntityType => _strategy.EntityName;

    public Affidavit Project(
        IContextFabric fabric,
        string operationType,
        IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(fabric);

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
                            if (chain.Current.Source != ProvenanceSource.Empty
                                && entity is not null
                                && entity.Fields.TryGetValue(field.Name, out var fv))
                            {
                                value = fv;
                            }
                        }
                        else
                        {
                            // Rule 7: never omit a field — tag it Empty rather than dropping it.
                            provenance = ProvenanceChain.From(ProvenanceTag.Empty);
                        }
                    }
                }

                var (kind, allowedValues) = ClassifyKind(field);

                return new AffidavitField(
                    field.Name, value, null, provenance, field.Required,
                    Kind: kind, AllowedValues: allowedValues, Pattern: field.Pattern);
            })
            .ToArray();

        var nonEmpty = fields.Where(f => f.Provenance.Current.Source != ProvenanceSource.Empty).ToArray();
        var aggregateConfidence = nonEmpty.Length == 0
            ? 0f
            : nonEmpty.Average(f => f.Provenance.Current.Confidence);

        var affidavit = new Affidavit(
            operationType,
            _strategy.EntityName,
            EntityId: null,
            fields,
            AggregateConfidence: aggregateConfidence,
            Warnings: warnings.ToArray(),
            RequiresConfirmation: true);

        // Compute summary metrics for telemetry.
        var populatedFieldCount = affidavit.Fields.Count(
            f => f.Value is not null && (f.Value is not string s || !string.IsNullOrEmpty(s)));
        var emptyProvenanceFieldCount = affidavit.Fields.Count(
            f => f.Provenance.Current.Source == ProvenanceSource.Empty);

        // Emit affidavit.projected span event with per-projection summary attributes.
        Activity.Current?.AddEvent(new ActivityEvent(
            "affidavit.projected",
            tags: new ActivityTagsCollection
            {
                { L2TelemetryKeys.AffidavitPopulatedFieldCount, populatedFieldCount },
                { L2TelemetryKeys.AffidavitAggregateConfidence, affidavit.AggregateConfidence },
                { L2TelemetryKeys.AffidavitEmptyProvenanceFieldCount, emptyProvenanceFieldCount },
            }));

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

        _eventStream.Publish(new AffidavitEmittedEvent(
            ConversationId: conversationId,
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

        var current = existingChain.Current;
        var candidateWins =
            candidate.Confidence > current.Confidence ||
            (candidate.Confidence == current.Confidence && (int)candidate.Source < (int)current.Source);

        return (existingChain.Merge(candidate), candidateWins);
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
