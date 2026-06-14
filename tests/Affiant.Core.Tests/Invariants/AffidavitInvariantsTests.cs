namespace Affiant.Core.Tests.Invariants;

// Framework-level invariant test (Task 7.2, deferred from Epic 16 to Epic 19 per the Epic 16
// preamble §"Task 7.2 deferral"). Asserts that every write strategy's compliance fixture's
// happy-path case produces an Affidavit with at least one populated field carrying non-Empty
// provenance — the invariant that would have caught the b72c1fa regression (2026-04-30).

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.Core.Tests.Invariants.TestFixtures;
using Affiant.SemanticKernel.Extensions;
using Affiant.Testing.ComplianceHarness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class AffidavitInvariantsTests
{
    // Structured-output JSON the fake port returns for a happy-path WorkOrder create.
    // Format: {FieldName: {value: "...", confidence: 0.0}} — expected by TaskInferenceStep.
    private const string HappyPathJson = """
        {
            "Title":     { "value": "Replace aircraft engine", "confidence": 0.95 },
            "Priority":  { "value": "High",                   "confidence": 0.90 },
            "AircraftId":{ "value": "A7-BCA",                 "confidence": 1.00 }
        }
        """;

    [Fact]
    public void WriteStrategyAffidavits_HavePopulatedFieldsWithNonEmptyProvenance()
    {
        // Arrange: synthetic test host — AddAffiantCore + AddAffiantInferenceOrchestration
        // with a fake IInferenceCompletionPort registered first so TryAddScoped leaves it in place.
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddSingleton<IInferenceCompletionPort>(new FakePort(HappyPathJson));
        services.AddAffiantInferenceOrchestration();
        services.AddAffiantTool<FakeWorkOrderStrategy>("CreateWorkOrder", Operation.WriteCreate, "WorkOrder");
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeWorkOrderComplianceFixture());

        using var provider = services.BuildServiceProvider();

        // Act — Phase 1: harness precondition check per L2 PRD §8.2.
        // Verify runs BEFORE the invariant; a missing fixture or failing fixture assertion
        // produces an informative message and stops the test here.
        var verifyResult = ComplianceHarness.Verify(services);

        Assert.True(
            verifyResult.Passed,
            $"ComplianceHarness.Verify failed:{Environment.NewLine}" +
            $"Missing Fixtures: {FormatMissingFixtures(verifyResult.MissingFixtures)}{Environment.NewLine}" +
            $"Fixture Failures: {FormatFixtureFailures(verifyResult.FixtureFailures)}");

        // Act — Phase 2: L2 PRD §7.2 structural invariant.
        // For every registered WriteCreate/WriteUpdate strategy with a paired fixture, run the
        // fixture's first (happy-path) case and assert the Affidavit has at least one populated
        // field with non-Empty provenance (Normative Rule #7: sworn provenance for every AI write).
        var registry = provider.GetRequiredService<IAffiantToolRegistry>();
        var writeDescriptors = registry.All
            .Where(d => (d.Operation.Kind == "WriteCreate" || d.Operation.Kind == "WriteUpdate")
                     && d.InferenceStrategy is not null)
            .ToList();

        foreach (var descriptor in writeDescriptors)
        {
            var fixture = provider.GetServices<ITaskInferenceComplianceFixture>()
                .FirstOrDefault(f => f.Strategy == descriptor.InferenceStrategy);
            if (fixture is null) continue;

            var happyPathCase = fixture.Cases.FirstOrDefault();
            if (happyPathCase is null) continue;

            var affidavit = ExecuteCase(provider, descriptor, happyPathCase);

            var populatedNonEmpty = affidavit.Fields
                .Where(f => f.Value is not null && f.Provenance.Current.Source != ProvenanceSource.Empty)
                .ToList();

            Assert.True(
                populatedNonEmpty.Count > 0,
                $"Strategy {descriptor.InferenceStrategy?.Name} (function {descriptor.FunctionName}) " +
                $"produced an Affidavit with no populated fields carrying non-Empty provenance — " +
                $"violation of Normative Rule #7 (sworn provenance for every AI write). " +
                $"Total fields: {affidavit.Fields.Length}. " +
                $"Empty-provenance fields: {affidavit.Fields.Count(f => f.Provenance.Current.Source == ProvenanceSource.Empty)}.");
        }
    }

    // Mirrors ComplianceHarness.ExecuteFixtureCase: creates an isolated ContextFabric per case,
    // runs TaskInferenceRunner to populate it, then projects an Affidavit via SchemaDrivenAffidavitProjection.
    private static Affidavit ExecuteCase(
        IServiceProvider provider,
        AffiantToolDescriptor descriptor,
        InferenceFixtureCase fixtureCase)
    {
        var port = provider.GetRequiredService<IInferenceCompletionPort>();
        var strategy = (ITaskInferenceStrategy)provider.GetRequiredService(descriptor.InferenceStrategy!);
        var eventStream = provider.GetRequiredService<IObservabilityEventStream<AffidavitEmittedEvent>>();
        var deterministicSources = provider.GetServices<IDeterministicFieldSource>();

        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var runner = new TaskInferenceRunner(port, fabric, step, NullLogger<TaskInferenceRunner>.Instance);

        runner.RunAsync(strategy, fixtureCase.History, descriptor.FunctionName, fixtureCase.Arguments)
              .GetAwaiter().GetResult();

        var projection = new SchemaDrivenAffidavitProjection(
            strategy,
            deterministicSources,
            NullLogger<SchemaDrivenAffidavitProjection>.Instance,
            eventStream);

        return projection.Project(fabric, descriptor.Operation.Kind, Array.Empty<string>());
    }

    private static string FormatMissingFixtures(IReadOnlyList<MissingFixture> missingFixtures)
    {
        if (missingFixtures.Count == 0) return "(none)";
        return string.Join(
            Environment.NewLine,
            missingFixtures.Select(mf => $"  - {mf.StrategyType.Name} (function {mf.FunctionName})"));
    }

    private static string FormatFixtureFailures(IReadOnlyList<FixtureFailure> fixtureFailures)
    {
        if (fixtureFailures.Count == 0) return "(none)";
        return string.Join(
            Environment.NewLine,
            fixtureFailures.Select(ff => $"  - {ff.StrategyType.Name}::{ff.FixtureCaseName}: {ff.Reason}"));
    }

    private sealed class FakePort(string json) : IInferenceCompletionPort
    {
        public Task<JsonElement> CompleteStructuredAsync(
            InferenceCompletionRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
    }
}
