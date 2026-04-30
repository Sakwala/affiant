namespace Affiant.Core.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Policies;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAffiantCore(this IServiceCollection services)
    {
        services.AddSingleton<IApprovalPolicy, ReviewerConfirmationPolicy>();
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();

        services.AddSingleton<DeterministicShortCircuit>();
        services.AddSingleton<IFunctionInvocationFilter>(
            sp => sp.GetRequiredService<DeterministicShortCircuit>());

        return services;
    }
}
