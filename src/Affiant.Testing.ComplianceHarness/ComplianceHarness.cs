namespace Affiant.Testing.ComplianceHarness;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

        // Phase 1: Discoverability — identify write strategies that have no paired fixture.
        var missingFixtures = writeDescriptors
            .Where(d => !fixtures.Any(f => f.Strategy == d.InferenceStrategy))
            .Select(d => new MissingFixture(d.InferenceStrategy!, d.FunctionName))
            .DistinctBy(mf => (mf.StrategyType, mf.FunctionName))
            .ToList();

        // Phase 2: Case execution — run each InferenceFixtureCase for every paired fixture.
        var fixtureFailures = new List<FixtureFailure>();

        var pairedStrategies = writeDescriptors
            .Select(d => d.InferenceStrategy!)
            .ToHashSet();

        foreach (var fixture in fixtures.Where(f => pairedStrategies.Contains(f.Strategy)))
        {
            // First matching descriptor supplies FunctionName and Operation.Kind for the projection call.
            var descriptor = writeDescriptors.First(d => d.InferenceStrategy == fixture.Strategy);

            foreach (var fixtureCase in fixture.Cases)
            {
                ExecuteFixtureCase(provider, fixture, fixtureCase, descriptor, fixtureFailures);
            }
        }

        return new ComplianceVerificationResult(
            Passed: missingFixtures.Count == 0 && fixtureFailures.Count == 0,
            MissingFixtures: missingFixtures,
            FixtureFailures: fixtureFailures);
    }

    private static void ExecuteFixtureCase(
        IServiceProvider provider,
        ITaskInferenceComplianceFixture fixture,
        InferenceFixtureCase fixtureCase,
        AffiantToolDescriptor descriptor,
        List<FixtureFailure> fixtureFailures)
    {
        // Per design note 4: absent port → report as failure, do not throw.
        var completionPort = provider.GetService<IInferenceCompletionPort>();
        if (completionPort is null)
        {
            fixtureFailures.Add(new FixtureFailure(
                fixture.Strategy,
                fixtureCase.Name,
                "no IInferenceCompletionPort registered — cannot execute fixture case"));
            return;
        }

        var strategyObj = provider.GetService(fixture.Strategy);
        if (strategyObj is not ITaskInferenceStrategy strategy)
        {
            fixtureFailures.Add(new FixtureFailure(
                fixture.Strategy,
                fixtureCase.Name,
                $"Strategy type {fixture.Strategy.Name} not registered in DI or does not implement ITaskInferenceStrategy"));
            return;
        }

        try
        {
            var loggerFactory = provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

            // Create isolated fabric for this case — each case must be independent.
            var fabric = new ContextFabric();
            var step = new TaskInferenceStep(
                fabric,
                loggerFactory.CreateLogger<TaskInferenceStep>());
            var runner = new TaskInferenceRunner(
                completionPort,
                fabric,
                step,
                loggerFactory.CreateLogger<TaskInferenceRunner>());

            try
            {
                runner.RunAsync(
                    strategy,
                    fixtureCase.History,
                    descriptor.FunctionName,
                    fixtureCase.Arguments)
                    .GetAwaiter().GetResult();
            }
            catch (AggregateException aex)
            {
                var inner = aex.InnerException ?? aex;
                fixtureFailures.Add(new FixtureFailure(
                    fixture.Strategy,
                    fixtureCase.Name,
                    $"Exception during case execution: {inner.GetType().Name}: {inner.Message}"));
                return;
            }

            var eventStream = provider.GetRequiredService<IObservabilityEventStream<AffidavitEmittedEvent>>();
            var deterministicSources = provider.GetServices<IDeterministicFieldSource>();
            var projection = new SchemaDrivenAffidavitProjection(
                strategy,
                deterministicSources,
                loggerFactory.CreateLogger<SchemaDrivenAffidavitProjection>(),
                eventStream);

            var affidavit = projection.Project(fabric, descriptor.Operation.Kind, Array.Empty<string>());

            bool assertionPassed;
            try
            {
                assertionPassed = fixtureCase.Assertion(affidavit);
            }
            catch (Exception ex)
            {
                fixtureFailures.Add(new FixtureFailure(
                    fixture.Strategy,
                    fixtureCase.Name,
                    $"Assertion threw: {ex.GetType().Name}: {ex.Message}"));
                return;
            }

            if (!assertionPassed)
            {
                fixtureFailures.Add(new FixtureFailure(
                    fixture.Strategy,
                    fixtureCase.Name,
                    "Assertion returned false"));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            fixtureFailures.Add(new FixtureFailure(
                fixture.Strategy,
                fixtureCase.Name,
                $"Exception during case execution: {ex.GetType().Name}: {ex.Message}"));
        }
    }
}
