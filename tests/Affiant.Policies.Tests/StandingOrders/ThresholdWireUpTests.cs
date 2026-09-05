namespace Affiant.Policies.Tests.StandingOrders;

using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Affiant.Core.Validation;
using Affiant.Policies.Extensions;
using Affiant.Policies.Services;
using Affiant.Policies.StandingOrders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// CV-1: "a policy that declares a threshold while no scorer is wired" is a <b>wire-up</b> refusal,
/// not a silent non-fire and not a first-evaluation throw.
/// </summary>
/// <remarks>
/// The framework ships no scoring formula — what counts as risk is the host's to say — so a Standing
/// Order that declares a ceiling with no calculator registered is a host that has not finished
/// wiring, and every write that order was written to auto-approve falls through to a person instead.
/// A boot-time helper the host had to remember to call is not the same thing as a refusal: a host
/// that never calls it gets no answer at all.
/// </remarks>
public sealed class ThresholdWireUpTests
{
    [Fact]
    public async Task AThresholdWithNoScorer_IsRefusedAtWireUp()
    {
        var validator = BuildValidator(services => services.AddAffiantPolicies(
            p => p.AddStandingOrder<CeilingOrder>()));

        var refused = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("SetRiskScoreCalculator", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThresholdWithAScorer_StartsCleanly()
    {
        var validator = BuildValidator(services => services.AddAffiantPolicies(p =>
        {
            p.AddStandingOrder<CeilingOrder>();
            p.SetRiskScoreCalculator<ConstantScorer>();
        }));

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnOrderThatDeclaresNoThreshold_NeedsNoScorer()
    {
        var validator = BuildValidator(services => services.AddAffiantPolicies(
            p => p.AddStandingOrder<NoCeilingOrder>()));

        await validator.StartAsync(CancellationToken.None);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An order that declares a ceiling. It takes the calculator the way beta.1's base constructor
    /// forced every such order to — as a constructor dependency — so the container injects whatever
    /// is registered, including the throwing placeholder when nothing is.
    /// </summary>
    private sealed class CeilingOrder(RiskScoreCalculatorBase? scorer = null)
        : StandingOrderBase(scorer)
    {
        protected override int? RiskThreshold => (int)RiskLevel.Low;

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class NoCeilingOrder : StandingOrderBase
    {
        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class ConstantScorer : RiskScoreCalculatorBase
    {
        public override Task<int> ComputeAsync(Affidavit affidavit, CancellationToken ct = default)
            => Task.FromResult((int)RiskLevel.Low);
    }

    private sealed class NoopReviewContextProvider : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => throw new NotSupportedException();
    }

    private sealed class NoopAuthorization : IDecisionAuthorizationPolicy
    {
        public Task<bool> MayDecideAsync(Principal principal, DocketEntry entry, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class WidgetStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";

        public IReadOnlyList<TaskInferenceField> Fields { get; } = [];

        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class NoopTransport : IStreamingTransport
    {
        public Task SendAsync(string c, TransportEvent e, object p, CancellationToken ct) => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string g, TransportEvent e, object p, CancellationToken ct)
            => Task.CompletedTask;

        public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(string s, Guid d, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>A host that has done everything else the validator asks for.</summary>
    private static AffiantWireUpValidator BuildValidator(Action<IServiceCollection> extra)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddAffiantCore();
        services.AddSingleton<IStreamingTransport>(new NoopTransport());
        services.AddSingleton<IDocketStore>(
            _ => throw new NotSupportedException("never resolved: the validator only asks whether it is registered"));
        services.AddScoped<ReviewGate>();
        services.AddSingleton<IReviewContextProvider, NoopReviewContextProvider>();
        services.AddDecisionAuthorization<NoopAuthorization>();
        services.AddAffiantTool<WidgetStrategy>("CreateWidget", Operation.WriteCreate, "Widget");
        extra(services);

        return services.BuildServiceProvider()
            .GetServices<IHostedService>().OfType<AffiantWireUpValidator>().Single();
    }
}
