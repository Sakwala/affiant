namespace Affiant.Core.Services;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IAffidavitProjection"/> driven by <see cref="ITaskInferenceStrategy.Fields"/>.
/// Per-field resolution order: deterministic source → ContextFabric chain → ProvenanceTag.Empty (Rule 7).
/// </summary>
public sealed class SchemaDrivenAffidavitProjection : IAffidavitProjection
{
    private readonly ITaskInferenceStrategy _strategy;
    private readonly IEnumerable<IDeterministicFieldSource> _deterministicSources;
    private readonly ILogger<SchemaDrivenAffidavitProjection> _logger;
    private readonly IObservabilityEventStream<AffidavitEmittedEvent> _eventStream;

    public SchemaDrivenAffidavitProjection(
        ITaskInferenceStrategy strategy,
        IEnumerable<IDeterministicFieldSource> deterministicSources,
        ILogger<SchemaDrivenAffidavitProjection> logger,
        IObservabilityEventStream<AffidavitEmittedEvent> eventStream)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _deterministicSources = deterministicSources ?? throw new ArgumentNullException(nameof(deterministicSources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventStream = eventStream ?? throw new ArgumentNullException(nameof(eventStream));
    }

    public string EntityType => _strategy.EntityName;

    public Affidavit Project(
        IContextFabric fabric,
        string operationType,
        IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(fabric);

        var entity = fabric.GetByKey(_strategy.EntityName);

        // Fields are emitted in strategy.Fields declared order for deterministic reviewer UX.
        var fields = _strategy.Fields
            .Select(field =>
            {
                ProvenanceChain provenance;
                object? value = null;

                // Deterministic source wins (spec §1.4 / PRD §2.4): first non-null Resolve result takes precedence.
                var deterministicTag = _deterministicSources
                    .Where(s => s.FieldName == field.Name)
                    .Select(s => s.Resolve(fabric))
                    .FirstOrDefault(t => t is not null);

                if (deterministicTag is not null)
                {
                    provenance = ProvenanceChain.From(deterministicTag);
                    if (entity is not null && entity.Fields.TryGetValue(field.Name, out var dv))
                        value = dv;
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

                return new AffidavitField(field.Name, value, null, provenance);
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
}
