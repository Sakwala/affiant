namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
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

    public SchemaDrivenAffidavitProjection(
        ITaskInferenceStrategy strategy,
        IEnumerable<IDeterministicFieldSource> deterministicSources,
        ILogger<SchemaDrivenAffidavitProjection> logger)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _deterministicSources = deterministicSources ?? throw new ArgumentNullException(nameof(deterministicSources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        return new Affidavit(
            operationType,
            _strategy.EntityName,
            EntityId: null,
            fields,
            AggregateConfidence: aggregateConfidence,
            Warnings: warnings.ToArray(),
            RequiresConfirmation: true);
    }
}
