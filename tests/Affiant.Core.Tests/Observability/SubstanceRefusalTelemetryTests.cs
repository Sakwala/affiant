namespace Affiant.Core.Tests.Observability;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Telemetry;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// GT-3's observable half at the projection seam: an Affidavit that swears to nothing raises
/// <c>affidavit.refused.substance</c> with the reason, and one that swears to something raises
/// nothing.
///
/// <para>
/// This release detects and reports; it does not yet refuse at run time — the runtime refusal lands
/// with the gate-pipeline change, at this same seam and with this same reason text. The event is
/// emitted now so an operator can build the alert against the release that ships the refusal.
/// </para>
/// </summary>
public class SubstanceRefusalTelemetryTests
{
    [Fact]
    public void EveryFieldEmpty_IsRefusedForSubstance()
    {
        using var probe = new TelemetryProbe();

        BuildProjection().Project(new ContextFabric(), "WriteCreate", []);

        var attributes = probe.Attributes(TelemetryKeys.AffidavitRefusedSubstance);
        Assert.Equal(
            "no proposed field carries provenance other than Empty",
            attributes[TelemetryKeys.Attributes.Reason]);
        Assert.Equal(2, attributes[TelemetryKeys.Attributes.AffidavitFieldCount]);
    }

    [Fact]
    public void ASubstantiveAffidavit_RaisesNoRefusal()
    {
        using var probe = new TelemetryProbe();

        var fabric = new ContextFabric();
        fabric.SetFieldChain("Color", ProvenanceChain.From(ProvenanceTag.FromInference("Color", 0.8f)));
        fabric.SetFieldChain("Weight", ProvenanceChain.From(ProvenanceTag.FromInference("Weight", 0.6f)));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["Color"] = "Red",
            ["Weight"] = "1.5",
        }));

        BuildProjection().Project(fabric, "WriteCreate", []);

        Assert.False(probe.Saw(TelemetryKeys.AffidavitRefusedSubstance));
    }

    /// <summary>
    /// The deprecated alias keeps firing for one release. An operator's dashboard built on
    /// <c>affidavit.projected</c> must not go dark the moment they upgrade — that is the whole point
    /// of a deprecation window, and this test is what makes the window real rather than a promise in
    /// a changelog.
    /// </summary>
    [Fact]
    public void TheDeprecatedProjectedEvent_IsStillEmittedAlongside()
    {
        using var probe = new TelemetryProbe();

        BuildProjection().Project(new ContextFabric(), "WriteCreate", []);

#pragma warning disable CS0618 // asserting the deprecated alias still fires is this test's whole job.
        Assert.True(probe.Saw(DeprecatedTelemetryKeys.AffidavitProjected));
#pragma warning restore CS0618
        Assert.True(probe.Saw(TelemetryKeys.AffidavitRefusedSubstance));
    }

    private static SchemaDrivenAffidavitProjection BuildProjection() =>
        new(new TwoFieldStrategy(), [], [],
            NullLogger<SchemaDrivenAffidavitProjection>.Instance,
            new InMemoryObservabilityEventStream<AffidavitEmittedEvent>());

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
