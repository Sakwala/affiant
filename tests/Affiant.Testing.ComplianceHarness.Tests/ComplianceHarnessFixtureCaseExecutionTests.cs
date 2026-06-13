namespace Affiant.Testing.ComplianceHarness.Tests;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

// ---------------------------------------------------------------------------
// Strategy with one field — enables meaningful merge-step coverage in tests.
// ---------------------------------------------------------------------------

internal sealed class FakeCaseStrategy : ITaskInferenceStrategy
{
    public string EntityName => "FakeCase";
    public IReadOnlyList<TaskInferenceField> Fields =>
    [
        new TaskInferenceField("Title", "string", "A title"),
    ];
    public double? MinimumConfidenceThreshold => null;
}

// ---------------------------------------------------------------------------
// Fake port — returns caller-controlled JSON so tests are deterministic.
// ---------------------------------------------------------------------------

internal sealed class FakeInferenceCompletionPort : IInferenceCompletionPort
{
    private readonly string _json;

    public FakeInferenceCompletionPort(string json) => _json = json;

    public Task<JsonElement> CompleteStructuredAsync(
        InferenceCompletionRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(JsonDocument.Parse(_json).RootElement.Clone());
}

// ---------------------------------------------------------------------------
// Configurable fixture — cases are supplied at construction time.
// ---------------------------------------------------------------------------

internal sealed class FakeCaseFixture : ITaskInferenceComplianceFixture
{
    private readonly InferenceFixtureCase[] _cases;

    public FakeCaseFixture(params InferenceFixtureCase[] cases) => _cases = cases;

    public Type Strategy => typeof(FakeCaseStrategy);
    public IEnumerable<InferenceFixtureCase> Cases => _cases;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

file static class Helpers
{
    // JSON that causes the merge step to populate the "Title" field in the fabric.
    public const string ValidTitleJson =
        """{"Title": {"value": "Test value", "confidence": 0.95}}""";

    public static IServiceCollection CreateBase()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddAffiantTool<FakeCaseStrategy>("CreateFakeCase", Operation.WriteCreate, "FakeCase");
        return services;
    }

    public static InferenceFixtureCase MakeCase(string name, Func<Affidavit, bool> assertion) =>
        new(name, new ChatHistory(), new Dictionary<string, object?>(), assertion);
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class ComplianceHarnessFixtureCaseExecutionTests
{
    // 1. Happy path: assertion passes → no FixtureFailure, Passed == true.
    [Fact]
    public void PassingCase_NoFixtureFailure_PassedIsTrue()
    {
        var services = Helpers.CreateBase();
        services.AddSingleton<IInferenceCompletionPort>(
            new FakeInferenceCompletionPort(Helpers.ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            Helpers.MakeCase("happy_path", a => a.EntityType == "FakeCase")));

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed);
        Assert.Empty(result.MissingFixtures);
        Assert.Empty(result.FixtureFailures);
    }

    // 2. Assertion returns false → named FixtureFailure with "Assertion returned false" reason.
    [Fact]
    public void FailingAssertion_FixtureFailureRecorded_WithExpectedReason()
    {
        var services = Helpers.CreateBase();
        services.AddSingleton<IInferenceCompletionPort>(
            new FakeInferenceCompletionPort(Helpers.ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            Helpers.MakeCase("failing_case", _ => false)));

        var result = ComplianceHarness.Verify(services);

        Assert.False(result.Passed);
        var failure = Assert.Single(result.FixtureFailures);
        Assert.Equal("failing_case", failure.FixtureCaseName);
        Assert.Equal(typeof(FakeCaseStrategy), failure.StrategyType);
        Assert.Equal("Assertion returned false", failure.Reason);
    }

    // 3. Assertion throws → FixtureFailure with exception details; exception not propagated.
    [Fact]
    public void AssertionThrows_FixtureFailureRecorded_ExceptionNotPropagated()
    {
        var services = Helpers.CreateBase();
        services.AddSingleton<IInferenceCompletionPort>(
            new FakeInferenceCompletionPort(Helpers.ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            Helpers.MakeCase("throwing_case", _ => throw new InvalidOperationException("test failure"))));

        var result = ComplianceHarness.Verify(services);

        Assert.False(result.Passed);
        var failure = Assert.Single(result.FixtureFailures);
        Assert.Equal("throwing_case", failure.FixtureCaseName);
        Assert.Contains("Assertion threw: InvalidOperationException: test failure", failure.Reason);
    }

    // 4. Missing IInferenceCompletionPort → FixtureFailure with "no IInferenceCompletionPort registered";
    //    Verify does not throw (design note 4).
    [Fact]
    public void MissingPort_FixtureFailureRecorded_VerifyDoesNotThrow()
    {
        var services = Helpers.CreateBase();
        // Intentionally no IInferenceCompletionPort registration.
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            Helpers.MakeCase("no_port", _ => true)));

        var result = ComplianceHarness.Verify(services);

        Assert.False(result.Passed);
        var failure = Assert.Single(result.FixtureFailures);
        Assert.Equal("no_port", failure.FixtureCaseName);
        Assert.Contains("no IInferenceCompletionPort registered", failure.Reason);
    }

    // 5. Mixed scenario: one strategy missing a fixture + one strategy with a failing case
    //    → both MissingFixtures and FixtureFailures populated, Passed == false.
    [Fact]
    public void MixedScenario_BothListsPopulated_PassedIsFalse()
    {
        var services = Helpers.CreateBase();
        // FakeThingStrategy has no fixture → appears in MissingFixtures.
        services.AddAffiantTool<FakeThingStrategy>("CreateThing", Operation.WriteCreate, "Thing");
        // FakeCaseStrategy has a fixture whose case fails → appears in FixtureFailures.
        services.AddSingleton<IInferenceCompletionPort>(
            new FakeInferenceCompletionPort(Helpers.ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            Helpers.MakeCase("failing_mixed", _ => false)));

        var result = ComplianceHarness.Verify(services);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.MissingFixtures);
        Assert.Contains(result.MissingFixtures, mf => mf.StrategyType == typeof(FakeThingStrategy));
        Assert.NotEmpty(result.FixtureFailures);
        Assert.Contains(result.FixtureFailures, ff => ff.FixtureCaseName == "failing_mixed");
    }

    // 6. Multiple cases per fixture: first passes, second fails → only the failing case recorded.
    [Fact]
    public void MultipleCases_FirstPassesSecondFails_OnlyFailingCaseRecorded()
    {
        var services = Helpers.CreateBase();
        services.AddSingleton<IInferenceCompletionPort>(
            new FakeInferenceCompletionPort(Helpers.ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            Helpers.MakeCase("case_one", _ => true),
            Helpers.MakeCase("case_two", _ => false)));

        var result = ComplianceHarness.Verify(services);

        Assert.False(result.Passed);
        var failure = Assert.Single(result.FixtureFailures);
        Assert.Equal("case_two", failure.FixtureCaseName);
    }

    // 7. Fixture with no cases → no iteration, no failures.
    [Fact]
    public void FixtureWithNoCases_NoFailures_PassedIsTrue()
    {
        var services = Helpers.CreateBase();
        services.AddSingleton<IInferenceCompletionPort>(
            new FakeInferenceCompletionPort(Helpers.ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(/* empty */));

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed);
        Assert.Empty(result.FixtureFailures);
    }

    // 8. Design note 2: concrete-only strategy (no ITaskInferenceStrategy binding) is still
    //    paired via the descriptor registry and its fixture cases are executed.
    [Fact]
    public void ConcreteOnlyStrategy_FixtureStillExecutedViaRegistry()
    {
        // AddAffiantTool<T> always uses TryAddSingleton<T> (concrete, not ITaskInferenceStrategy),
        // mirroring hosts that bind strategies as their concrete type only.
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddAffiantTool<FakeCaseStrategy>("CreateFakeCase", Operation.WriteCreate, "FakeCase");
        services.AddSingleton<IInferenceCompletionPort>(
            new FakeInferenceCompletionPort(Helpers.ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            Helpers.MakeCase("concrete_only", a => a.EntityType == "FakeCase")));

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed);
        Assert.Empty(result.FixtureFailures);
    }

    // 9. Field value populated by the merge step is visible in the Affidavit assertion.
    [Fact]
    public void InferredFieldValue_VisibleInAffidavit_AssertionPassesOnValue()
    {
        var services = Helpers.CreateBase();
        services.AddSingleton<IInferenceCompletionPort>(
            new FakeInferenceCompletionPort(Helpers.ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            Helpers.MakeCase("field_value", a =>
            {
                var title = a.Fields.FirstOrDefault(f => f.Name == "Title");
                return title is not null && title.Value?.ToString() == "Test value";
            })));

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed);
        Assert.Empty(result.FixtureFailures);
    }
}
