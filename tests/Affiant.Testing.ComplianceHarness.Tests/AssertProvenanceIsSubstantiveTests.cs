namespace Affiant.Testing.ComplianceHarness.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// ---------------------------------------------------------------------------
// Fakes for the substance gate.
// ---------------------------------------------------------------------------

// A strategy whose sole field is declared Required — exercises the Required→IsMandatory projection gate.
internal sealed class FakeRequiredFieldStrategy : ITaskInferenceStrategy
{
    public string EntityName => "RequiredEntity";
    public IReadOnlyList<TaskInferenceField> Fields =>
    [
        new TaskInferenceField("Name", "string", "A required name", Required: true),
    ];
    public double? MinimumConfidenceThreshold => null;
}

/// <summary>
/// Tests for <see cref="ComplianceHarness.AssertProvenanceIsSubstantive"/> — the executable guard
/// against the b72c1fa regression class (structurally-valid but substantively-hollow Affidavits).
///
/// The direct-predicate tests exercise the four per-Affidavit checks over hand-built Affidavits;
/// the Verify-level tests prove the gate runs by default and is independent of the fixture's own
/// (possibly weak) hand-written assertion.
/// </summary>
public class AssertProvenanceIsSubstantiveTests
{
    private static readonly ITaskInferenceStrategy RequiredStrategy = new FakeRequiredFieldStrategy();

    private static ProvenanceChain InferredChain(float confidence = 0.9f) =>
        ProvenanceChain.From(ProvenanceTag.FromInference("Name", confidence));

    private static Affidavit AffidavitWith(params AffidavitField[] fields) =>
        new("WriteCreate", "RequiredEntity", EntityId: null, fields,
            AggregateConfidence: 0.9f, Warnings: [], RequiresConfirmation: true);

    // ── Direct-predicate: the four per-Affidavit checks ──────────────────────

    // Check 1: an empty Fields array is the empty-Affidavit form of b72c1fa.
    [Fact]
    public void EmptyFields_ReportsAffidavitLevelFailure()
    {
        var affidavit = AffidavitWith(/* no fields */);

        var failures = ComplianceHarness.AssertProvenanceIsSubstantive(RequiredStrategy, "empty_fields", affidavit);

        var failure = Assert.Single(failures);
        Assert.Equal(typeof(FakeRequiredFieldStrategy), failure.StrategyType);
        Assert.Equal("empty_fields", failure.FixtureCaseName);
        Assert.Equal("(affidavit)", failure.FieldName);
        Assert.Contains("Fields is empty", failure.Reason);
    }

    // Check 2: a field emitted with a null provenance chain violates Rule 7.
    [Fact]
    public void NullProvenanceChain_ReportsRule7Failure()
    {
        var affidavit = AffidavitWith(
            new AffidavitField("Name", "Ada", PreviousValue: null, Provenance: null!, IsMandatory: true));

        var failures = ComplianceHarness.AssertProvenanceIsSubstantive(RequiredStrategy, "null_chain", affidavit);

        Assert.Contains(failures, f => f.FieldName == "Name" && f.Reason.Contains("Rule 7"));
    }

    // Check 3: a populated value carrying Empty provenance is the hollow signature.
    [Fact]
    public void ValueWithEmptyProvenance_ReportsFieldFailure_NamingStrategyCaseField()
    {
        var affidavit = AffidavitWith(
            new AffidavitField("Name", "Ada", PreviousValue: null,
                Provenance: ProvenanceChain.From(ProvenanceTag.Empty), IsMandatory: true));

        var failures = ComplianceHarness.AssertProvenanceIsSubstantive(RequiredStrategy, "value_no_prov", affidavit);

        var failure = Assert.Single(failures);
        Assert.Equal(typeof(FakeRequiredFieldStrategy), failure.StrategyType);
        Assert.Equal("value_no_prov", failure.FixtureCaseName);
        Assert.Equal("Name", failure.FieldName);
        Assert.Contains("Ada", failure.Reason);
    }

    // Check 4: a Required strategy field that projects to IsMandatory == false fails.
    [Fact]
    public void RequiredFieldNotMandatory_ReportsFieldFailure()
    {
        var affidavit = AffidavitWith(
            new AffidavitField("Name", "Ada", PreviousValue: null,
                Provenance: InferredChain(), IsMandatory: false));

        var failures = ComplianceHarness.AssertProvenanceIsSubstantive(RequiredStrategy, "not_mandatory", affidavit);

        var failure = Assert.Single(failures);
        Assert.Equal("Name", failure.FieldName);
        Assert.Contains("Required=true", failure.Reason);
        Assert.Contains("IsMandatory", failure.Reason);
    }

    // Healthy substantive Affidavit: value present, sworn (Inferred), Required→Mandatory holds.
    [Fact]
    public void HealthySubstantiveAffidavit_NoFailures()
    {
        var affidavit = AffidavitWith(
            new AffidavitField("Name", "Ada", PreviousValue: null,
                Provenance: InferredChain(), IsMandatory: true));

        var failures = ComplianceHarness.AssertProvenanceIsSubstantive(RequiredStrategy, "healthy", affidavit);

        Assert.Empty(failures);
    }

    // Design invariant: a below-threshold case that legitimately infers nothing yields an all-Empty
    // Affidavit (value null everywhere). This must PASS the per-case gate — the "produce substance
    // somewhere" requirement is enforced per-fixture in Verify, not per-case here.
    [Fact]
    public void HollowButValuelessAffidavit_PassesPerCaseGate()
    {
        var affidavit = AffidavitWith(
            new AffidavitField("Name", Value: null, PreviousValue: null,
                Provenance: ProvenanceChain.From(ProvenanceTag.Empty), IsMandatory: true));

        var failures = ComplianceHarness.AssertProvenanceIsSubstantive(RequiredStrategy, "below_threshold", affidavit);

        Assert.Empty(failures);
    }

    // ── Verify-level: the gate runs by default and is assertion-independent ───

    // The key non-decorative property: a fixture whose hand-written assertion is trivially true
    // ("_ => true") but whose only case is hollow still FAILS Verify via the substance gate.
    [Fact]
    public void Verify_HollowFixture_FailsViaSubstanceGate_EvenWhenAssertionPasses()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddAffiantTool<FakeCaseStrategy>("CreateFakeCase", Operation.WriteCreate, "FakeCase");
        // Port returns an empty object → the single "Title" field is never merged → all-Empty Affidavit.
        services.AddSingleton<IInferenceCompletionPort>(new FakeInferenceCompletionPort("{}"));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            new InferenceFixtureCase(
                "hollow_but_asserts_true",
                Array.Empty<AffiantChatMessage>(),
                new Dictionary<string, object?>(),
                _ => true)));

        var result = ComplianceHarness.Verify(services);

        Assert.False(result.Passed);
        Assert.Empty(result.FixtureFailures); // the fixture's own assertion passed
        var substance = Assert.Single(result.SubstanceFailures);
        Assert.Equal(typeof(FakeCaseStrategy), substance.StrategyType);
        Assert.Equal("(all cases)", substance.FixtureCaseName);
        Assert.Contains("hollow", substance.Reason);
    }

    // A healthy fixture whose happy-path case produces substantive provenance passes with no
    // substance failures.
    [Fact]
    public void Verify_HealthyFixture_Passes_NoSubstanceFailures()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddAffiantTool<FakeCaseStrategy>("CreateFakeCase", Operation.WriteCreate, "FakeCase");
        services.AddSingleton<IInferenceCompletionPort>(new FakeInferenceCompletionPort(
            """{"Title": {"value": "Real value", "confidence": 0.95}}"""));
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeCaseFixture(
            new InferenceFixtureCase(
                "happy_path",
                Array.Empty<AffiantChatMessage>(),
                new Dictionary<string, object?>(),
                a => a.Fields.Length > 0)));

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed);
        Assert.Empty(result.SubstanceFailures);
    }
}
