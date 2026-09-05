namespace Affiant.Core.Tests.Observability;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Verifies that SchemaDrivenAffidavitProjection.Project emits the affidavit.projected
/// span event with the three summary attributes and also sets the populated_field_count
/// tag on Activity.Current. Counts are validated against the actual field values in the
/// projected Affidavit.
/// </summary>
public class SchemaDrivenAffidavitProjectionTelemetryTests
{
    // ── Listener helpers ──────────────────────────────────────────────────────

    private static (ActivityListener Listener, Activity? Root) StartListening()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name is "Affiant.Framework" or "Affiant.TaskInference",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var root = AffiantTelemetry.AffiantActivitySource.StartActivity("test_root");
        return (listener, root);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Project_EmitsAffidavitProjectedEvent_WithThreeAttributes()
    {
        var (listener, root) = StartListening();

        try
        {
            var projection = BuildProjection();
            var fabric = new ContextFabric();
            projection.Project(fabric, "WriteCreate", []);
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        var events = root?.Events.ToList() ?? [];
        Assert.True(events.Any(e => e.Name == "affidavit.projected"),
            "Expected affidavit.projected event to be emitted");
        var projected = events.Single(e => e.Name == "affidavit.projected");
        var tags = projected.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.True(tags.ContainsKey("affiant.affidavit.populated_field_count"),
            "affiant.affidavit.populated_field_count attribute missing");
        Assert.True(tags.ContainsKey("affiant.affidavit.aggregate_confidence"),
            "affiant.affidavit.aggregate_confidence attribute missing");
        Assert.True(tags.ContainsKey("affiant.affidavit.empty_provenance_field_count"),
            "affiant.affidavit.empty_provenance_field_count attribute missing");
    }

    [Fact]
    public void Project_AllEmptyFabric_PopulatedFieldCountZero_EmptyProvenanceCountMatchesFieldCount()
    {
        var (listener, root) = StartListening();

        try
        {
            var projection = BuildProjection();
            var fabric = new ContextFabric(); // empty — all fields get ProvenanceSource.Empty
            projection.Project(fabric, "WriteCreate", []);
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        var events = root?.Events.ToList() ?? [];
        var projected = events.Single(e => e.Name == "affidavit.projected");
        var tags = projected.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(0, Convert.ToInt32(tags["affiant.affidavit.populated_field_count"]));
        Assert.Equal(0f, Convert.ToSingle(tags["affiant.affidavit.aggregate_confidence"]), precision: 5);
        Assert.Equal(2, Convert.ToInt32(tags["affiant.affidavit.empty_provenance_field_count"]));
    }

    [Fact]
    public void Project_PopulatedFabric_FieldCountsCorrect()
    {
        var (listener, root) = StartListening();

        try
        {
            var projection = BuildProjection();
            var fabric = new ContextFabric();
            // Both fields populated via inference — neither is Empty provenance.
            fabric.SetFieldChain("Color", ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "Color", 0.8f)));
            fabric.SetFieldChain("Weight", ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "Weight", 0.6f)));
            fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
            {
                ["Color"] = "Red",
                ["Weight"] = "1.5",
            }));

            projection.Project(fabric, "WriteCreate", []);
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        var events = root?.Events.ToList() ?? [];
        var projected = events.Single(e => e.Name == "affidavit.projected");
        var tags = projected.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(2, Convert.ToInt32(tags["affiant.affidavit.populated_field_count"]));
        Assert.Equal(0, Convert.ToInt32(tags["affiant.affidavit.empty_provenance_field_count"]));
        // AggregateConfidence = min(0.8, 0.6) = 0.6
        Assert.Equal(0.6f, Convert.ToSingle(tags["affiant.affidavit.aggregate_confidence"]), precision: 5);
    }

    [Fact]
    public void Project_SetsPopulatedFieldCountTagOnCurrentSpan()
    {
        var (listener, root) = StartListening();

        try
        {
            var projection = BuildProjection();
            var fabric = new ContextFabric();
            fabric.SetFieldChain("Color", ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "Color", 0.9f)));
            fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
            {
                ["Color"] = "Blue",
            }));

            projection.Project(fabric, "WriteCreate", []);
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        // The tag must also be set on the span itself (not only in the event).
        var tag = root?.GetTagItem("affiant.affidavit.populated_field_count");
        Assert.NotNull(tag);
        Assert.Equal(1, Convert.ToInt32(tag));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SchemaDrivenAffidavitProjection BuildProjection()
    {
        var strategy = new TwoFieldStrategy();
        var eventStream = new InMemoryObservabilityEventStream<AffidavitEmittedEvent>();
        return new SchemaDrivenAffidavitProjection(
            strategy, [], [],
            NullLogger<SchemaDrivenAffidavitProjection>.Instance,
            eventStream);
    }

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
}
