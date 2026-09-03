using Affiant.Abstractions.Interfaces;
using Affiant.Policies.StandingOrders;
using Microsoft.Extensions.DependencyInjection;

namespace Affiant.Policies;

/// <summary>
/// Host-callable checks over the registered approval policies.
/// </summary>
public static class AffiantPolicies
{
    /// <summary>
    /// Resolves every registered <see cref="IApprovalPolicy"/> in a throwaway scope and checks
    /// each Standing Order's risk configuration: an order that declares a <c>RiskThreshold</c>
    /// with no <c>RiskScoreCalculatorBase</c> registered throws
    /// <see cref="InvalidOperationException"/> naming <c>SetRiskScoreCalculator&lt;T&gt;()</c>.
    ///
    /// The same check runs at the top of every Standing Order's evaluation, so calling this is
    /// optional — it only moves the failure earlier. Call it once after the host is built to turn
    /// a misconfiguration into a boot failure instead of a first-request one. It evaluates no
    /// Affidavit and approves nothing.
    /// </summary>
    /// <param name="serviceProvider">The built application's root service provider.</param>
    public static void ValidateStandingOrders(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        using var scope = serviceProvider.CreateScope();

        foreach (var policy in scope.ServiceProvider.GetServices<IApprovalPolicy>())
        {
            if (policy is StandingOrderBase order)
                order.EnsureConfigured();
        }
    }
}
