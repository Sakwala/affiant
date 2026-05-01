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
    /// </summary>
    public static IServiceCollection AddAffiantPolicies(
        this IServiceCollection services,
        Action<PoliciesBuilder>? configure = null)
    {
        // Register the default RiskScoreCalculator unless the host has overridden it.
        services.TryAddScoped<RiskScoreCalculator, DefaultRiskScoreCalculator>();

        configure?.Invoke(new PoliciesBuilder(services));

        return services;
    }
}

/// <summary>
/// Fluent builder for registering approval policies in declaration order.
/// Policies are evaluated by <c>ApprovalPolicyEvaluator</c> in the order they are added.
/// Specific Standing Orders and Referral rules should come before the catch-all
/// <see cref="AddDefaultReviewerConfirmation"/> call.
/// </summary>
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
    /// Replaces the registered <see cref="RiskScoreCalculator"/> with a custom implementation.
    /// Call before <see cref="AddStandingOrder{TPolicy}"/> if Standing Orders depend on the scorer.
    /// </summary>
    public PoliciesBuilder SetRiskScoreCalculator<TCalculator>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TCalculator : RiskScoreCalculator
    {
        _services.RemoveAll<RiskScoreCalculator>();
        _services.Add(new ServiceDescriptor(typeof(RiskScoreCalculator), typeof(TCalculator), lifetime));
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
    public Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
        => Task.FromResult<ReviewRequirement?>(ReviewRequirement.ReviewerConfirmation);
}
