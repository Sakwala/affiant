namespace Affiant.Policies.Tests.StandingOrders;

using System.Diagnostics;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Telemetry;
using Affiant.Core.Observability;
using Affiant.Policies.Services;
using Affiant.Policies.StandingOrders;
using Xunit;

/// <summary>
/// A Standing Order approving a write with no person present is the most consequential thing a
/// policy does, and a Standing Order that did not fire is the reason a person is being asked. Both
/// have registry keys (AZ-1, GT-5), and both are emitted from
/// <see cref="StandingOrderBase.EvaluateAsync"/>.
/// </summary>
public class StandingOrderTelemetryTests
{
    [Fact]
    public async Task AnOrderThatFires_EmitsStandingOrderFired()
    {
        using var probe = new TelemetryProbe();

        await new LowRiskOrder().EvaluateAsync(NoFields());

        var attributes = probe.Attributes(TelemetryKeys.StandingOrderFired);
        Assert.Equal(typeof(LowRiskOrder).FullName, attributes[TelemetryKeys.Attributes.PolicyId]);
        Assert.Equal((int)RiskLevel.Low, attributes[TelemetryKeys.Attributes.RiskScore]);
        Assert.False(probe.Saw(TelemetryKeys.StandingOrderBlocked));
    }

    [Fact]
    public async Task AnOrderBlockedByItsThreshold_EmitsTheStableReasonCode()
    {
        using var probe = new TelemetryProbe();

        var requirement = await new DefaultScoredOrder().EvaluateAsync(HighValue());

        Assert.Null(requirement);
        var attributes = probe.Attributes(TelemetryKeys.StandingOrderBlocked);
        Assert.Equal("risk-above-threshold", attributes[TelemetryKeys.Attributes.BlockedReason]);
        Assert.Equal((int)RiskLevel.Low, attributes[TelemetryKeys.Attributes.RiskThreshold]);
        Assert.False(probe.Saw(TelemetryKeys.StandingOrderFired));

        // The sentence and the code are separate attributes on purpose: a dashboard alerts on the
        // code, and the sentence stays free to be rewritten for whoever reads the card.
        Assert.NotEqual(
            attributes[TelemetryKeys.Attributes.BlockedReason],
            attributes[TelemetryKeys.Attributes.Reason]);
    }

    [Fact]
    public async Task AnOrderWhoseConditionsDoNotMatch_EmitsNothing()
    {
        using var probe = new TelemetryProbe();

        await new NeverMatchingOrder().EvaluateAsync(NoFields());

        Assert.False(probe.Saw(TelemetryKeys.StandingOrderFired));
        Assert.False(probe.Saw(TelemetryKeys.StandingOrderBlocked));
    }

    [Fact]
    public async Task APolicyThatVersionsItself_SaysSoOnTheEvent()
    {
        using var probe = new TelemetryProbe();

        await new VersionedOrder().EvaluateAsync(NoFields());

        var attributes = probe.Attributes(TelemetryKeys.StandingOrderFired);
        Assert.Equal("payments-v3", attributes[TelemetryKeys.Attributes.PolicyId]);
        Assert.Equal("3", attributes[TelemetryKeys.Attributes.PolicyVersion]);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A host-supplied scorer that answers Low.
    ///
    /// <para>
    /// It has to exist for these tests to reach the fired path at all: the shipped
    /// <see cref="RiskScoreCalculatorBase"/> default never returns
    /// <see cref="RiskLevel.Low"/> while <see cref="StandingOrderBase.RiskThreshold"/> defaults to
    /// Low, so a by-the-book Standing Order built on the defaults can never fire. That is the
    /// defect rule GT-5 names, and moving the risk function to the host is the gate change's job,
    /// not this one's — recorded here so the workaround is not mistaken for the test being contrived.
    /// </para>
    /// </summary>
    private sealed class LowScoringCalculator : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
            => Task.FromResult((int)RiskLevel.Low);
    }

    private sealed class LowRiskOrder() : StandingOrderBase(new LowScoringCalculator())
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class DefaultScoredOrder() : StandingOrderBase(new DefaultRiskScoreCalculator())
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class NeverMatchingOrder() : StandingOrderBase(new DefaultRiskScoreCalculator())
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(false);
    }

    private sealed class VersionedOrder() : StandingOrderBase(new LowScoringCalculator())
    {
        protected override string PolicyId => "payments-v3";

        protected override string? PolicyVersion => "3";

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    /// <summary>
    /// The same isolated listener the Core suite uses — see its copy for why the source is touched
    /// before the listener is registered (repo issue #17).
    /// </summary>
    private sealed class TelemetryProbe : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Activity? _root;

        public TelemetryProbe()
        {
            var source = AffiantTelemetry.AffiantActivitySource;

            _listener = new ActivityListener
            {
                ShouldListenTo = candidate => ReferenceEquals(candidate, source),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(_listener);
            _root = source.StartActivity("test_root");
        }

        public IReadOnlyList<ActivityEvent> Events => _root?.Events.ToList() ?? [];

        public bool Saw(string name) => Events.Any(e => e.Name == name);

        public IReadOnlyDictionary<string, object?> Attributes(string name) =>
            Events.Single(e => e.Name == name).Tags.ToDictionary(t => t.Key, t => t.Value);

        public void Dispose()
        {
            _root?.Dispose();
            _listener.Dispose();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static Affidavit NoFields() => new(
        OperationType: "Test", EntityType: "TestEntity", EntityId: null, Fields: [],
        AggregateConfidence: 1.0f, Warnings: [], RequiresConfirmation: false);

    private static Affidavit HighValue() => new(
        OperationType: "Test", EntityType: "TestEntity", EntityId: null,
        Fields: [new AffidavitField("Value", 100m, null, ProvenanceChain.From(ProvenanceTag.Empty))],
        AggregateConfidence: 1.0f, Warnings: [], RequiresConfirmation: false);
}
