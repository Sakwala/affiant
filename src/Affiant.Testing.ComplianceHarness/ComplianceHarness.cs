namespace Affiant.Testing.ComplianceHarness;

using Affiant.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;

public static class ComplianceHarness
{
    public static ComplianceVerificationResult Verify(IServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAffiantToolRegistry>();

        var writeDescriptors = registry.All
            .Where(d => (d.Operation.Kind == "WriteCreate" || d.Operation.Kind == "WriteUpdate")
                     && d.InferenceStrategy is not null)
            .ToList();

        var fixtures = provider.GetServices<ITaskInferenceComplianceFixture>().ToList();

        var missingFixtures = writeDescriptors
            .Where(d => !fixtures.Any(f => f.Strategy == d.InferenceStrategy))
            .Select(d => new MissingFixture(d.InferenceStrategy!, d.FunctionName))
            .DistinctBy(mf => (mf.StrategyType, mf.FunctionName))
            .ToList();

        return new ComplianceVerificationResult(
            Passed: missingFixtures.Count == 0,
            MissingFixtures: missingFixtures,
            FixtureFailures: []);
    }
}
