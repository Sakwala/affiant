namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The shape the built-in projection swears to, one test per checkable sentence of the two rules it
/// serves.
///
/// <para>
/// The field list carries exactly the proposed fields — every one present, no other present, and a
/// proposed field whose provenance is unknown present and tagged <c>Empty</c> at confidence 0 rather
/// than quietly omitted.
/// </para>
///
/// <para>
/// The entity id is non-null if and only if the operation is update-shaped; an update carries a
/// previous value on every proposed field, holding the entity's stored value or null where it had
/// none, and those values come from the host's port, which is consulted for updates only.
/// </para>
/// </summary>
public class SchemaDrivenAffidavitProjectionShapeTests
{
    private sealed class WidgetStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Colour", "string", "Colour of the widget"),
            new("Weight", "string", "Weight in kg"),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    /// <summary>A strategy with one card field and one extraction field (never on the card).</summary>
    private sealed class WidgetWithExtractionStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Colour", "string", "Colour of the widget"),
            new("TailNumber", "string", "Mentioned in passing", Projected: false),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    /// <summary>
    /// A host port that records what it was asked. <paramref name="serves"/> non-null means "I only
    /// answer for this entity type" — anything else gets the null that means "not mine, ask the next
    /// source".
    /// </summary>
    private sealed class RecordingPreviousValues(
        string? serves = null,
        IReadOnlyDictionary<string, object?>? values = null) : IPreviousValueSource
    {
        public List<(string EntityType, string EntityId)> Calls { get; } = [];

        public Task<IReadOnlyDictionary<string, object?>?> GetPreviousValuesAsync(
            string entityType, string entityId, CancellationToken cancellationToken)
        {
            Calls.Add((entityType, entityId));

            if (serves is not null && !string.Equals(serves, entityType, StringComparison.Ordinal))
                return Task.FromResult<IReadOnlyDictionary<string, object?>?>(null);

            return Task.FromResult<IReadOnlyDictionary<string, object?>?>(
                values ?? new Dictionary<string, object?>());
        }
    }

    private static SchemaDrivenAffidavitProjection BuildProjection(
        ITaskInferenceStrategy? strategy = null,
        IEnumerable<IPreviousValueSource>? previousValues = null) =>
        new(
            strategy ?? new WidgetStrategy(),
            [],
#pragma warning disable CS0618 // The legacy deterministic-source seam, still functional.
            [],
#pragma warning restore CS0618
            NullLogger<SchemaDrivenAffidavitProjection>.Instance,
            new InMemoryObservabilityEventStream<AffidavitEmittedEvent>(),
            previousValues);

    private static ContextFabric PopulatedFabric()
    {
        var fabric = new ContextFabric();
        fabric.SetFieldChain(
            "Colour",
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "Colour", 0.8f)));
        fabric.SetFieldChain(
            "Weight",
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "Weight", 0.7f)));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["Colour"] = "red",
            ["Weight"] = "1.5",
        }));
        return fabric;
    }

    // ── The field list ───────────────────────────────────────────────────────

    [Fact]
    public void EveryFieldTheOperationProposes_IsPresent()
    {
        var affidavit = BuildProjection().Project(new ContextFabric(), "WriteCreate", []);

        Assert.Equal(["Colour", "Weight"], affidavit.Fields.Select(f => f.Name));
    }

    [Fact]
    public void AFieldTheOperationDoesNotPropose_IsAbsent_NeverEmptyTagged()
    {
        var fabric = new ContextFabric();
        fabric.SetFieldChain(
            "TailNumber",
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "TailNumber", 0.9f)));

        var affidavit = BuildProjection(new WidgetWithExtractionStrategy())
            .Project(fabric, "WriteCreate", []);

        // TailNumber is extracted and available to a resolver, but the write does not propose it —
        // so it is absent rather than present with an Empty tag.
        Assert.Equal("Colour", Assert.Single(affidavit.Fields).Name);
    }

    [Fact]
    public void AProposedFieldWithUnknownProvenance_IsPresentAndEmptyTaggedAtZero()
    {
        var affidavit = BuildProjection().Project(new ContextFabric(), "WriteCreate", []);

        Assert.All(affidavit.Fields, f =>
        {
            Assert.Equal(ProvenanceSource.Empty, f.Provenance.Current.Source);
            Assert.Equal(0f, f.Provenance.Current.Confidence);
        });
    }

    // ── The operation shape ──────────────────────────────────────────────────

    [Fact]
    public void ACreate_NamesNoEntity_AndEveryPreviousValueIsNull()
    {
        var affidavit = BuildProjection().Project(PopulatedFabric(), "WriteCreate", []);

        Assert.Null(affidavit.EntityId);
        Assert.All(affidavit.Fields, f => Assert.Null(f.PreviousValue));
    }

    [Fact]
    public void AnUpdate_NamesTheEntityItUpdates()
    {
        var affidavit = BuildProjection(previousValues: [new RecordingPreviousValues()])
            .Project(PopulatedFabric(), "WriteUpdate", [], entityId: "widget-1");

        Assert.Equal("widget-1", affidavit.EntityId);
    }

    [Fact]
    public void AnUpdate_CarriesTheStoredValueOnEveryProposedField()
    {
        var source = new RecordingPreviousValues(values: new Dictionary<string, object?>
        {
            ["Colour"] = "blue",
            ["Weight"] = "1.2",
        });

        var affidavit = BuildProjection(previousValues: [source])
            .Project(PopulatedFabric(), "WriteUpdate", [], entityId: "widget-1");

        Assert.Equal("blue", affidavit.Fields.Single(f => f.Name == "Colour").PreviousValue);
        Assert.Equal("1.2", affidavit.Fields.Single(f => f.Name == "Weight").PreviousValue);
    }

    [Fact]
    public void AnUpdate_CarriesNullWhereTheEntityHadNoStoredValue()
    {
        var source = new RecordingPreviousValues(values: new Dictionary<string, object?>
        {
            ["Colour"] = "blue",
            // Weight is simply not there.
        });

        var affidavit = BuildProjection(previousValues: [source])
            .Project(PopulatedFabric(), "WriteUpdate", [], entityId: "widget-1");

        Assert.Equal("blue", affidavit.Fields.Single(f => f.Name == "Colour").PreviousValue);
        Assert.Null(affidavit.Fields.Single(f => f.Name == "Weight").PreviousValue);
    }

    [Fact]
    public void ThePreviousValuePort_IsConsultedForUpdatesOnly()
    {
        var source = new RecordingPreviousValues();
        var projection = BuildProjection(previousValues: [source]);

        projection.Project(PopulatedFabric(), "WriteCreate", []);
        Assert.Empty(source.Calls);

        projection.Project(PopulatedFabric(), "WriteUpdate", [], entityId: "widget-1");
        Assert.Equal(("Widget", "widget-1"), Assert.Single(source.Calls));
    }

    [Fact]
    public void ThePreviousValuePort_IsAskedInRegistrationOrder_FirstNonNullAnswerWins()
    {
        var wrongStore = new RecordingPreviousValues(serves: "Gadget");
        var rightStore = new RecordingPreviousValues(values: new Dictionary<string, object?>
        {
            ["Colour"] = "blue",
        });

        var affidavit = BuildProjection(previousValues: [wrongStore, rightStore])
            .Project(PopulatedFabric(), "WriteUpdate", [], entityId: "widget-1");

        Assert.Single(wrongStore.Calls);
        Assert.Single(rightStore.Calls);
        Assert.Equal("blue", affidavit.Fields.Single(f => f.Name == "Colour").PreviousValue);
    }

    [Fact]
    public void AnUpdateWithNoEntityId_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BuildProjection().Project(PopulatedFabric(), "WriteUpdate", []));

        Assert.Equal("entityId", ex.ParamName);
    }

    [Fact]
    public void ACreateThatNamesAnEntity_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BuildProjection().Project(PopulatedFabric(), "WriteCreate", [], entityId: "widget-1"));

        Assert.Equal("entityId", ex.ParamName);
    }

    [Theory]
    [InlineData("WriteUpdate", true)]
    [InlineData("writeupdate", true)]
    [InlineData("update", true)]
    [InlineData("WriteCreate", false)]
    [InlineData("WriteDelete", false)]
    [InlineData("UpdateCustomer", false)]
    [InlineData(null, false)]
    public void TheUpdateShapePredicate_RecognisesTheProtocolsTwoSpellings(string? kind, bool expected)
    {
        Assert.Equal(expected, Operation.IsUpdateShaped(kind));
    }

    // ── The three numbers, at filing ─────────────────────────────────────────

    [Fact]
    public void TheThreeNumbers_AreComputedAtFiling()
    {
        var fabric = new ContextFabric();
        fabric.SetFieldChain(
            "Colour",
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "Colour", 0.8f)));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["Colour"] = "red",
        }));

        var affidavit = BuildProjection().Project(fabric, "WriteCreate", []);

        Assert.Equal(0f, affidavit.AggregateConfidence, 5);
        Assert.Equal(0.8f, affidavit.PopulatedConfidence!.Value, 5);
        Assert.Equal(1, affidavit.EmptyFieldCount);
    }
}
