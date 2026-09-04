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
    /// <summary>
    /// A by-the-book Standing Order — match, no risk ceiling — fires, and says so. No
    /// <c>risk.score</c> attribute: nothing was scored, and an absent attribute is honest where a
    /// zero would read as "scored, and it was zero".
    /// </summary>
    [Fact]
    public async Task AnOrderThatFires_EmitsStandingOrderFired()
    {
        using var probe = new TelemetryProbe();

        await new ByTheBookOrder().EvaluateAsync(NoFields(), TestIdentities.Anyone);

        var attributes = probe.Attributes(TelemetryKeys.StandingOrderFired);
        Assert.Equal(typeof(ByTheBookOrder).FullName, attributes[TelemetryKeys.Attributes.PolicyId]);
        Assert.False(attributes.ContainsKey(TelemetryKeys.Attributes.RiskScore));
        Assert.False(probe.Saw(TelemetryKeys.StandingOrderBlocked));
    }

    [Fact]
    public async Task AnOrderThatFiresUnderItsCeiling_CarriesTheScoreItWasJudgedOn()
    {
        using var probe = new TelemetryProbe();

        await new UnderThresholdOrder().EvaluateAsync(NoFields(), TestIdentities.Anyone);

        var attributes = probe.Attributes(TelemetryKeys.StandingOrderFired);
        Assert.Equal((int)RiskLevel.Low, attributes[TelemetryKeys.Attributes.RiskScore]);
        Assert.False(probe.Saw(TelemetryKeys.StandingOrderBlocked));
    }

    [Fact]
    public async Task AnOrderBlockedByItsThreshold_EmitsTheStableReasonCode()
    {
        using var probe = new TelemetryProbe();

        var verdict = await new OverThresholdOrder().EvaluateAsync(HighValue(), TestIdentities.Anyone);

        // The order matched and had an opinion, so it degrades to ReviewerConfirmation rather
        // than returning null and letting a later policy speak as though it never fired.
        Assert.Equal(ReviewRequirement.ReviewerConfirmation, verdict!.Requirement);
        Assert.Equal(ReviewRequirement.StandingOrder, verdict.DegradedFrom);
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

        await new NeverMatchingOrder().EvaluateAsync(NoFields(), TestIdentities.Anyone);

        Assert.False(probe.Saw(TelemetryKeys.StandingOrderFired));
        Assert.False(probe.Saw(TelemetryKeys.StandingOrderBlocked));
    }

    [Fact]
    public async Task APolicyThatVersionsItself_SaysSoOnTheEvent()
    {
        using var probe = new TelemetryProbe();

        await new VersionedOrder().EvaluateAsync(NoFields(), TestIdentities.Anyone);

        var attributes = probe.Attributes(TelemetryKeys.StandingOrderFired);
        Assert.Equal("payments-v3", attributes[TelemetryKeys.Attributes.PolicyId]);
        Assert.Equal("3", attributes[TelemetryKeys.Attributes.PolicyVersion]);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────────────────────

    /// <summary>A host-supplied scorer that answers Low — under any Low ceiling.</summary>
    private sealed class LowScoringCalculator : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
            => Task.FromResult((int)RiskLevel.Low);
    }

    /// <summary>Match and nothing else — the shape the framework documents. It needs no scorer.</summary>
    private sealed class ByTheBookOrder : StandingOrderBase
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class UnderThresholdOrder() : StandingOrderBase(new LowScoringCalculator())
    {
        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    /// <summary>A host-supplied scorer that answers High — over any Low ceiling.</summary>
    private sealed class HighScoringCalculator : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
            => Task.FromResult((int)RiskLevel.High);
    }

    private sealed class OverThresholdOrder() : StandingOrderBase(new HighScoringCalculator())
    {
        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class NeverMatchingOrder() : StandingOrderBase(new HighScoringCalculator())
    {
        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(false);
    }

    private sealed class VersionedOrder() : StandingOrderBase(new LowScoringCalculator())
    {
        public override string PolicyId => "payments-v3";

        public override string? PolicyVersion => "3";

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

    private static Affidavit NoFields() => Affidavit.Create(
        operationType: "Test", entityType: "TestEntity", entityId: null, fields: [],
        warnings: [], requiresConfirmation: false);

    private static Affidavit HighValue() => Affidavit.Create(
        operationType: "Test", entityType: "TestEntity", entityId: null,
        fields: [new AffidavitField("Value", 100m, null,
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, "Value", 0.8f)))],
        warnings: [], requiresConfirmation: false);
}
