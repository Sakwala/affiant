using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Policies.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Affiant.Policies.Extensions;

/// <summary>
/// DI extension for registering Affiant.Policies infrastructure.
/// </summary>
/// <example>
/// <code>
/// services.AddAffiantPolicies(policies =>
/// {
///     policies
///         .AddStandingOrder&lt;LowValueAutoApproval&gt;()
///         .AddReferralRule&lt;HighValueEscalation&gt;()
///         .AddDefaultReviewerConfirmation();
/// });
/// </code>
/// </example>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Affiant.Policies infrastructure. Call the builder to declare
    /// Standing Orders, Referral rules, and the default confirmation fallback.
    /// No scoring formula is registered here — the framework has none of its own. Supply one with
    /// <see cref="PoliciesBuilder.SetRiskScoreCalculator{TCalculator}"/> if any Standing Order
    /// declares a risk threshold.
    /// </summary>
    public static IServiceCollection AddAffiantPolicies(
        this IServiceCollection services,
        Action<PoliciesBuilder>? configure = null)
    {
        configure?.Invoke(new PoliciesBuilder(services));

        // A placeholder, not a formula: every call to it throws, naming
        // SetRiskScoreCalculator<T>(). It is registered so that a Standing Order whose
        // constructor takes RiskScoreCalculatorBase as a *required* dependency still resolves —
        // otherwise the container refuses it with "Unable to resolve service for type
        // 'RiskScoreCalculatorBase'", which names no fix. TryAdd, and last in the method, so a
        // calculator the host registered — through SetRiskScoreCalculator<T>() above or directly
        // on the IServiceCollection — always wins. Registered Singleton, not Scoped: it is
        // stateless and every call throws, so there is nothing scope-shaped about it — and a
        // Singleton avoids making a Standing Order that depends on it a captive dependency should
        // that order itself ever be registered Singleton.
        services.TryAddSingleton<RiskScoreCalculatorBase, MissingRiskScoreCalculator>();

        return services;
    }
}

/// <summary>
/// Fluent builder for registering approval policies in declaration order.
/// Policies are evaluated by <c>ApprovalPolicyEvaluator</c> in the order they are added.
/// Specific Standing Orders and Referral rules should come before the catch-all
/// <see cref="AddDefaultReviewerConfirmation"/> call.
/// </summary>
/// <remarks>
/// The default <see cref="ServiceLifetime.Scoped"/> lifetime below is safe precisely because
/// <c>Affiant.Core</c>'s <c>ApprovalPolicyEvaluator</c> (the sole consumer of
/// <c>IEnumerable&lt;IApprovalPolicy&gt;</c>) is itself registered Scoped (affiant#19) — a policy
/// with a Scoped dependency (e.g. a host <c>DbContext</c>) no longer risks becoming a captive
/// dependency of a longer-lived evaluator.
/// </remarks>
public sealed class PoliciesBuilder
{
    private readonly IServiceCollection _services;

    public PoliciesBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Registers a Standing Order policy. Evaluated before any later-registered policies.
    /// </summary>
    public PoliciesBuilder AddStandingOrder<TPolicy>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TPolicy : class, IApprovalPolicy
    {
        _services.Add(new ServiceDescriptor(typeof(IApprovalPolicy), typeof(TPolicy), lifetime));
        return this;
    }

    /// <summary>
    /// Registers a Referral rule policy. Evaluated before any later-registered policies.
    /// </summary>
    public PoliciesBuilder AddReferralRule<TRule>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TRule : class, IApprovalPolicy
    {
        _services.Add(new ServiceDescriptor(typeof(IApprovalPolicy), typeof(TRule), lifetime));
        return this;
    }

    /// <summary>
    /// Registers the host's <see cref="RiskScoreCalculatorBase"/>, replacing any already
    /// registered. Required by every Standing Order that declares a risk threshold; a
    /// threshold-less Standing Order needs no calculator.
    ///
    /// Call it anywhere in the builder chain: it removes every existing
    /// <see cref="RiskScoreCalculatorBase"/> registration before adding its own, and the
    /// placeholder <c>AddAffiantPolicies</c> falls back to is only registered when the host
    /// registered none. Standing Orders are registered under a different service type and are
    /// constructed after the whole chain has run, so this wins whether it is called before or
    /// after <see cref="AddStandingOrder{TPolicy}"/>. The last call wins if it is called twice.
    /// </summary>
    public PoliciesBuilder SetRiskScoreCalculator<TCalculator>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TCalculator : RiskScoreCalculatorBase
    {
        _services.RemoveAll<RiskScoreCalculatorBase>();
        _services.Add(new ServiceDescriptor(typeof(RiskScoreCalculatorBase), typeof(TCalculator), lifetime));
        return this;
    }

    /// <summary>
    /// Registers a catch-all policy that always returns <see cref="ReviewRequirement.ReviewerConfirmation"/>.
    /// Add this last to preserve the "always require human review" default.
    /// Without it, <c>ApprovalPolicyEvaluator</c>'s built-in fallback still returns ReviewerConfirmation.
    /// </summary>
    public PoliciesBuilder AddDefaultReviewerConfirmation()
    {
        _services.Add(new ServiceDescriptor(
            typeof(IApprovalPolicy),
            new DefaultReviewerConfirmationPolicy()));
        return this;
    }
}

/// <summary>
/// Catch-all policy that always returns ReviewerConfirmation.
/// Provides explicit backward compatibility with Phase 1 "always require confirmation" semantics.
/// </summary>
internal sealed class DefaultReviewerConfirmationPolicy : IApprovalPolicy
{
    public Task<ApprovalVerdict?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
        => Task.FromResult<ApprovalVerdict?>(ReviewRequirement.ReviewerConfirmation);
}
