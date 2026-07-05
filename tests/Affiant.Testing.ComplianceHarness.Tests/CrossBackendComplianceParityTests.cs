namespace Affiant.Testing.ComplianceHarness.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Testing.ComplianceHarness.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Cross-backend compliance parity (proposal affiant-maf-adapter.md §6): every ComplianceHarness
/// fixture-case scenario and the AssertProvenanceIsSubstantive gate must behave identically whether
/// the registered <see cref="IInferenceCompletionPort"/> is the SK bridge
/// (SemanticKernelInferenceCompletionPort) or the MAF bridge (AgentFrameworkInferenceCompletionPort)
/// — the guardrail that makes semantic drift between backends structurally impossible (proposal
/// §4.1: "one pipeline, two bridges"). Each [Theory] runs once per backend via
/// <see cref="InferenceCompletionPortProviderFactory"/>; the scripted LLM edge for each backend
/// answers the same fixed JSON a real model would for the scenario under test.
///
/// These scenarios mirror (not duplicate the intent of) ComplianceHarnessFixtureCaseExecutionTests
/// and AssertProvenanceIsSubstantiveTests, which already gate the neutral pipeline via the generic
/// FakeInferenceCompletionPort; the point here is proving the two shipped bridges reproduce those
/// same outcomes, not re-deriving new expectations. FakeCaseStrategy and FakeCaseFixture are the
/// same internal fixtures ComplianceHarnessFixtureCaseExecutionTests.cs declares — reused here by
/// assembly visibility, not redefined.
/// </summary>
public class CrossBackendComplianceParityTests
{
    private const string ValidTitleJson =
        """{"Title": {"value": "Test value", "confidence": 0.95}}""";

    private static IServiceCollection CreateBaseServices(IInferenceCompletionPort port)
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddAffiantTool<FakeCaseStrategy>("CreateFakeCase", Operation.WriteCreate, "FakeCase");
        services.AddSingleton(port);
        return services;
    }

    private static InferenceFixtureCase MakeCase(string name, Func<Affidavit, bool> assertion) =>
        new(name, Array.Empty<AffiantChatMessage>(), new Dictionary<string, object?>(), assertion);

    [Theory]
    [ClassData(typeof(InferenceCompletionPortProviderFactory))]
    public void PassingCase_NoFixtureFailure_PassedIsTrue(
        Func<string, IInferenceCompletionPort> buildPort, string providerName)
    {
        var services = CreateBaseServices(buildPort(ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            MakeCase("happy_path", a => a.EntityType == "FakeCase")));

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed, $"{providerName}: expected Passed=true");
        Assert.Empty(result.FixtureFailures);
        Assert.Empty(result.SubstanceFailures);
    }

    [Theory]
    [ClassData(typeof(InferenceCompletionPortProviderFactory))]
    public void FailingAssertion_FixtureFailureRecorded_WithExpectedReason(
        Func<string, IInferenceCompletionPort> buildPort, string providerName)
    {
        var services = CreateBaseServices(buildPort(ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            MakeCase("failing_case", _ => false)));

        var result = ComplianceHarness.Verify(services);

        Assert.False(result.Passed, $"{providerName}: expected Passed=false");
        var failure = Assert.Single(result.FixtureFailures);
        Assert.Equal("failing_case", failure.FixtureCaseName);
        Assert.Equal(typeof(FakeCaseStrategy), failure.StrategyType);
        Assert.Equal("Assertion returned false", failure.Reason);
    }

    [Theory]
    [ClassData(typeof(InferenceCompletionPortProviderFactory))]
    public void InferredFieldValue_VisibleInAffidavit_AssertionPassesOnValue(
        Func<string, IInferenceCompletionPort> buildPort, string providerName)
    {
        var services = CreateBaseServices(buildPort(ValidTitleJson));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            MakeCase("field_value", a =>
            {
                var title = a.Fields.FirstOrDefault(f => f.Name == "Title");
                return title is not null && title.Value?.ToString() == "Test value";
            })));

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed, $"{providerName}: expected Passed=true");
        Assert.Empty(result.FixtureFailures);
    }

    [Theory]
    [ClassData(typeof(InferenceCompletionPortProviderFactory))]
    public void Verify_HollowFixture_FailsViaSubstanceGate_EvenWhenAssertionPasses(
        Func<string, IInferenceCompletionPort> buildPort, string providerName)
    {
        // Port returns an empty object => the single "Title" field is never merged => all-Empty
        // Affidavit — the b72c1fa hollow-Affidavit regression shape, reproduced through both bridges.
        var services = CreateBaseServices(buildPort("{}"));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            MakeCase("hollow_but_asserts_true", _ => true)));

        var result = ComplianceHarness.Verify(services);

        Assert.False(result.Passed, $"{providerName}: expected Passed=false");
        Assert.Empty(result.FixtureFailures); // the fixture's own assertion passed
        var substance = Assert.Single(result.SubstanceFailures);
        Assert.Equal(typeof(FakeCaseStrategy), substance.StrategyType);
        Assert.Equal("(all cases)", substance.FixtureCaseName);
        Assert.Contains("hollow", substance.Reason);
    }

    [Theory]
    [ClassData(typeof(InferenceCompletionPortProviderFactory))]
    public void Verify_HealthyFixture_Passes_NoSubstanceFailures(
        Func<string, IInferenceCompletionPort> buildPort, string providerName)
    {
        var services = CreateBaseServices(
            buildPort("""{"Title": {"value": "Real value", "confidence": 0.95}}"""));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            MakeCase("happy_path", a => a.Fields.Length > 0)));

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed, $"{providerName}: expected Passed=true");
        Assert.Empty(result.SubstanceFailures);
    }
}
