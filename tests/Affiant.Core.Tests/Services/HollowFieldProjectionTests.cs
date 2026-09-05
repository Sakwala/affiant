namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// GT-3's hollow signature: a field asserting a value while its provenance reads
/// <see cref="ProvenanceSource.Empty"/>. The projection must carry that field as it stands — the
/// value AND the Empty tag — so the gate can refuse it. A projection that dropped the value would
/// turn a hollow proposal into an empty one, and the refusal a reviewer's operator reads would name
/// the wrong signature: "this proposal knows nothing" instead of "this field claims something and
/// swears nothing about where it came from".
/// </summary>
public class HollowFieldProjectionTests
{
    private sealed class WidgetStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";

        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Colour", "string", "Colour of the widget"),
        ];

        public double? MinimumConfidenceThreshold => null;
    }

    private static SchemaDrivenAffidavitProjection Projection() =>
        new(
            new WidgetStrategy(),
            [],
#pragma warning disable CS0618 // The legacy deterministic-source seam, still functional.
            [],
#pragma warning restore CS0618
            NullLogger<SchemaDrivenAffidavitProjection>.Instance,
            new InMemoryObservabilityEventStream<AffidavitEmittedEvent>());

    private static ContextFabric HollowFabric()
    {
        var fabric = new ContextFabric();
        fabric.SetFieldChain("Colour", ProvenanceChain.From(ProvenanceTag.Empty));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["Colour"] = "red",
        }));
        return fabric;
    }

    [Fact]
    public void AValueWithAnEmptyTag_KeepsItsValue()
    {
        var affidavit = Projection().Project(HollowFabric(), "WriteCreate", []);

        var field = Assert.Single(affidavit.Fields);
        Assert.Equal("red", field.Value);
        Assert.Equal(ProvenanceSource.Empty, field.Provenance.Current.Source);
    }

    [Fact]
    public void AValueWithAnEmptyTag_ReadsAsHollow_NotAsSwearingToNothing()
    {
        var affidavit = Projection().Project(HollowFabric(), "WriteCreate", []);

        Assert.Equal(
            "field \"Colour\" carries a value with Empty provenance",
            AffidavitSubstance.DescribeFailure(affidavit));
    }

    [Fact]
    public void AFieldWithNoChainAtAll_CarriesNoValue()
    {
        var fabric = new ContextFabric();
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["Colour"] = "red",
        }));

        var affidavit = Projection().Project(fabric, "WriteCreate", []);

        // Nothing said anything about this field, so there is nothing to carry: a value with no
        // provenance at all is exactly the claim the Empty tag denies (AF-1).
        var field = Assert.Single(affidavit.Fields);
        Assert.Null(field.Value);
        Assert.Equal(ProvenanceSource.Empty, field.Provenance.Current.Source);
    }
}
