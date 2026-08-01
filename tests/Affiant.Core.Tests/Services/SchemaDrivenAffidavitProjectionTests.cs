namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#pragma warning disable CS0618 // Testing the soft-deprecated IDeterministicFieldSource, kept fully functional (P2 area-1 wave)

public class SchemaDrivenAffidavitProjectionTests
{
    // --- Fakes ---

    private sealed class TwoFieldStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Color", "string", "Color of the widget"),
            new("Weight", "string", "Weight in kg"),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class MandatoryFieldStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Color", "string", "Color of the widget", Required: true),
            new("Weight", "string", "Weight in kg"),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class MetadataFieldStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Status", "string", "Widget status", Enum: ["Active", "Retired"]),
            new("Weight", "number", "Weight in kg", Pattern: @"^\d+(\.\d+)?$"),
            new("ManufacturedOn", "string", "Manufacture date", Format: "date"),
            new("Description", "string", "Free-text description"),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class FixedSource : IDeterministicFieldSource
    {
        private readonly ProvenanceTag? _tag;
        public string FieldName { get; }

        public FixedSource(string fieldName, ProvenanceTag? tag)
        {
            FieldName = fieldName;
            _tag = tag;
        }

        public ProvenanceTag? Resolve(IContextFabric fabric) => _tag;
    }

    private static SchemaDrivenAffidavitProjection BuildProjection(
        ITaskInferenceStrategy? strategy = null,
        IEnumerable<IDeterministicFieldSource>? sources = null,
        IEnumerable<IFieldResolver>? resolvers = null)
    {
        strategy ??= new TwoFieldStrategy();
        sources ??= [];
        resolvers ??= [];
        return new SchemaDrivenAffidavitProjection(
            strategy, resolvers, sources, NullLogger<SchemaDrivenAffidavitProjection>.Instance,
            new InMemoryObservabilityEventStream<AffidavitEmittedEvent>());
    }

    // --- Test 1: all-empty fabric → every field is Empty, AggregateConfidence == 0f ---

    [Fact]
    public void AllEmptyFabric_AllFieldsEmpty_AggregateConfidenceZero()
    {
        var fabric = new ContextFabric();
        var projection = BuildProjection();

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        Assert.Equal(2, affidavit.Fields.Length);
        Assert.All(affidavit.Fields, f =>
        {
            Assert.Null(f.Value);
            Assert.Equal(ProvenanceSource.Empty, f.Provenance.Current.Source);
        });
        Assert.Equal(0f, affidavit.AggregateConfidence);
        Assert.True(affidavit.RequiresConfirmation);
    }

    // --- Test 2: populated fabric → fields carry their chains, AggregateConfidence = mean ---

    [Fact]
    public void PopulatedFabric_FieldsCarryChains_CorrectAggregateConfidence()
    {
        var fabric = new ContextFabric();
        var colorTag = ProvenanceTag.FromInference("Color", 0.8f);
        var weightTag = ProvenanceTag.FromInference("Weight", 0.6f);
        fabric.SetFieldChain("Color", ProvenanceChain.From(colorTag));
        fabric.SetFieldChain("Weight", ProvenanceChain.From(weightTag));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["Color"] = "Red",
            ["Weight"] = "1.5",
        }));

        var projection = BuildProjection();
        var affidavit = projection.Project(fabric, "WriteCreate", []);

        Assert.Equal(2, affidavit.Fields.Length);
        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.Equal("Red", colorField.Value);
        Assert.Equal(ProvenanceSource.Inferred, colorField.Provenance.Current.Source);
        Assert.Equal(0.7f, affidavit.AggregateConfidence, 5); // mean(0.8, 0.6)
    }

    // --- Test 3: deterministic source wins over fabric ---

    [Fact]
    public void DeterministicSource_WinsOverFabric()
    {
        var fabric = new ContextFabric();
        fabric.SetFieldChain("Color", ProvenanceChain.From(ProvenanceTag.FromInference("Color", 0.5f)));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["Color"] = "Blue",
        }));

        var deterministicTag = ProvenanceTag.FromUser("Color");
        var source = new FixedSource("Color", deterministicTag);
        var projection = BuildProjection(sources: [source]);

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.Equal(ProvenanceSource.UserStated, colorField.Provenance.Current.Source);
    }

    // --- Test 4: deterministic source returning null falls back to fabric ---

    [Fact]
    public void DeterministicSource_ReturnsNull_FallsBackToFabric()
    {
        var fabric = new ContextFabric();
        var fabricTag = ProvenanceTag.FromInference("Color", 0.7f);
        fabric.SetFieldChain("Color", ProvenanceChain.From(fabricTag));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["Color"] = "Green",
        }));

        var nullSource = new FixedSource("Color", null); // returns null → fabric fallback
        var projection = BuildProjection(sources: [nullSource]);

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.Equal(ProvenanceSource.Inferred, colorField.Provenance.Current.Source);
        Assert.Equal("Green", colorField.Value);
    }

    // --- Test 5: EntityType returns strategy.EntityName ---

    [Fact]
    public void EntityType_ReturnsStrategyEntityName()
    {
        var projection = BuildProjection();
        Assert.Equal("Widget", projection.EntityType);
    }

    // --- Test 6: Project never emits fewer than strategy.Fields.Count fields (Rule 7) ---

    [Fact]
    public void Project_NeverEmitsFewerFieldsThanStrategy()
    {
        var fabric = new ContextFabric(); // empty
        var projection = BuildProjection();

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        Assert.Equal(new TwoFieldStrategy().Fields.Count, affidavit.Fields.Length);
    }

    // --- Test 7: warnings are forwarded to the Affidavit ---

    [Fact]
    public void Project_ForwardsWarnings()
    {
        var fabric = new ContextFabric();
        var projection = BuildProjection();
        var warnings = new[] { "Field X is missing context", "Low confidence on Y" };

        var affidavit = projection.Project(fabric, "WriteCreate", warnings);

        Assert.Equal(warnings, affidavit.Warnings);
    }

    // --- Test 8: AggregateConfidence excludes Empty-sourced fields ---

    [Fact]
    public void AggregateConfidence_ExcludesEmptyFields()
    {
        var fabric = new ContextFabric();
        // Only Color has a real chain; Weight stays Empty
        fabric.SetFieldChain("Color", ProvenanceChain.From(ProvenanceTag.FromInference("Color", 0.9f)));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["Color"] = "Yellow",
        }));

        var projection = BuildProjection();
        var affidavit = projection.Project(fabric, "WriteCreate", []);

        // Only Color contributes → confidence = 0.9
        Assert.Equal(0.9f, affidavit.AggregateConfidence, 5);
    }

    // --- Test 9: strategy.Required threads to AffidavitField.IsMandatory (issue #2) ---

    [Fact]
    public void RequiredField_EmptyValue_ProjectsMandatory_ProvenanceUntouched()
    {
        var fabric = new ContextFabric(); // empty → both fields Empty-sourced
        var projection = BuildProjection(new MandatoryFieldStrategy());

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.True(colorField.IsMandatory);
        Assert.Null(colorField.Value);
        Assert.Equal(ProvenanceSource.Empty, colorField.Provenance.Current.Source);

        var weightField = affidavit.Fields.Single(f => f.Name == "Weight");
        Assert.False(weightField.IsMandatory);
    }

    // --- Test 10: constructor null guards ---

    [Fact]
    public void Constructor_NullStrategy_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SchemaDrivenAffidavitProjection(
                null!, [], [], NullLogger<SchemaDrivenAffidavitProjection>.Instance,
                new InMemoryObservabilityEventStream<AffidavitEmittedEvent>()));
    }

    // --- Test 11: metadata forwarding (D6) — Kind/AllowedValues/Pattern per TaskInferenceField ---

    [Fact]
    public void Project_ForwardsEnumMetadata()
    {
        var projection = BuildProjection(new MetadataFieldStrategy());
        var affidavit = projection.Project(new ContextFabric(), "WriteCreate", []);

        var status = affidavit.Fields.Single(f => f.Name == "Status");
        Assert.Equal(AffidavitFieldKind.Enum, status.Kind);
        Assert.Equal(["Active", "Retired"], status.AllowedValues);
        Assert.Null(status.Pattern);
    }

    [Fact]
    public void Project_ForwardsNumberMetadata_AndPattern()
    {
        var projection = BuildProjection(new MetadataFieldStrategy());
        var affidavit = projection.Project(new ContextFabric(), "WriteCreate", []);

        var weight = affidavit.Fields.Single(f => f.Name == "Weight");
        Assert.Equal(AffidavitFieldKind.Number, weight.Kind);
        Assert.Null(weight.AllowedValues);
        Assert.Equal(@"^\d+(\.\d+)?$", weight.Pattern);
    }

    [Fact]
    public void Project_ForwardsDateMetadata_FromFormatHint()
    {
        var projection = BuildProjection(new MetadataFieldStrategy());
        var affidavit = projection.Project(new ContextFabric(), "WriteCreate", []);

        var manufacturedOn = affidavit.Fields.Single(f => f.Name == "ManufacturedOn");
        Assert.Equal(AffidavitFieldKind.Date, manufacturedOn.Kind);
        Assert.Null(manufacturedOn.AllowedValues);
    }

    [Fact]
    public void Project_DefaultsToTextKind_WhenNoMetadataHints()
    {
        var projection = BuildProjection(new MetadataFieldStrategy());
        var affidavit = projection.Project(new ContextFabric(), "WriteCreate", []);

        var description = affidavit.Fields.Single(f => f.Name == "Description");
        Assert.Equal(AffidavitFieldKind.Text, description.Kind);
        Assert.Null(description.AllowedValues);
        Assert.Null(description.Pattern);
    }

    // --- Test 12: default behavior unchanged for fields without metadata (D6) ---

    [Fact]
    public void Project_FieldsWithoutMetadata_DefaultToTextKind_NullsForAllowedValuesAndPattern()
    {
        // TwoFieldStrategy's fields carry no Enum/Pattern/Format — confirms the additive
        // members don't perturb the pre-D6 default projection shape.
        var projection = BuildProjection();
        var affidavit = projection.Project(new ContextFabric(), "WriteCreate", []);

        Assert.All(affidavit.Fields, f =>
        {
            Assert.Equal(AffidavitFieldKind.Text, f.Kind);
            Assert.Null(f.AllowedValues);
            Assert.Null(f.Pattern);
        });
    }
}
