namespace Affiant.Core.Tests.Observability;

using System.Collections.Concurrent;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Verifies that SchemaDrivenAffidavitProjection.Project publishes exactly one
/// AffidavitEmittedEvent per call through IObservabilityEventStream{AffidavitEmittedEvent},
/// with field values that match the Affidavit, and that concurrent projections do not corrupt
/// subscriber state.
/// </summary>
public class AffidavitEmittedEventPublicationTests
{
    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Project_PublishesOneEventPerCall()
    {
        var stream = new InMemoryObservabilityEventStream<AffidavitEmittedEvent>();
        var received = new List<AffidavitEmittedEvent>();
        stream.Subscribe(e => received.Add(e));

        var projection = BuildProjection(stream);
        var fabric = new ContextFabric();
        projection.Project(fabric, "WriteCreate", []);

        Assert.Single(received);
    }

    [Fact]
    public void Project_PublishedEvent_FieldValuesMatchAffidavit()
    {
        var stream = new InMemoryObservabilityEventStream<AffidavitEmittedEvent>();
        AffidavitEmittedEvent? received = null;
        stream.Subscribe(e => received = e);

        var projection = BuildProjection(stream);
        var fabric = new ContextFabric();

        // Populate one field with a real value so PopulatedFieldCount = 1.
        fabric.SetFieldChain("Color", ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "Color", 0.8f)));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["Color"] = "Blue",
        }));

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        Assert.NotNull(received);
        Assert.Equal("WriteCreate", received.OperationType);
        Assert.Equal("Widget", received.EntityType);
        Assert.Equal(affidavit.AggregateConfidence, received.AggregateConfidence);
        // PopulatedFieldCount must match: Color has value, Weight is null → count = 1.
        Assert.Equal(1, received.PopulatedFieldCount);
        // EmptyProvenanceFieldCount: Weight is empty → count = 1.
        Assert.Equal(1, received.EmptyProvenanceFieldCount);
        // AffidavitId must be a non-empty Guid.
        Assert.NotEqual(Guid.Empty, received.AffidavitId);
    }

    [Fact]
    public void Project_MultipleSequentialCalls_EachProducesOneEvent()
    {
        var stream = new InMemoryObservabilityEventStream<AffidavitEmittedEvent>();
        var received = new List<AffidavitEmittedEvent>();
        stream.Subscribe(e => received.Add(e));

        var projection = BuildProjection(stream);

        for (var i = 0; i < 5; i++)
        {
            var fabric = new ContextFabric();
            projection.Project(fabric, "WriteCreate", []);
        }

        Assert.Equal(5, received.Count);
        // Each event must have a distinct AffidavitId.
        Assert.Equal(5, received.Select(e => e.AffidavitId).Distinct().Count());
    }

    [Fact]
    public async Task Project_ConcurrentCalls_AllEventsReceivedWithoutCorruption()
    {
        var stream = new InMemoryObservabilityEventStream<AffidavitEmittedEvent>();
        var received = new ConcurrentBag<AffidavitEmittedEvent>();
        stream.Subscribe(e => received.Add(e));

        var projection = BuildProjection(stream);

        const int concurrency = 20;
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(() =>
        {
            var fabric = new ContextFabric();
            projection.Project(fabric, "WriteCreate", []);
        })));

        Assert.Equal(concurrency, received.Count);
        // All events must have distinct AffidavitIds — Guid.NewGuid() per projection call.
        Assert.Equal(concurrency, received.Select(e => e.AffidavitId).Distinct().Count());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SchemaDrivenAffidavitProjection BuildProjection(
        IObservabilityEventStream<AffidavitEmittedEvent> eventStream)
    {
        var strategy = new TwoFieldStrategy();
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
