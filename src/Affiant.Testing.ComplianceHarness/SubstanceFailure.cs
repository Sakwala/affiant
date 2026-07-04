namespace Affiant.Testing.ComplianceHarness;

/// <summary>
/// A violation of substantive provenance detected by <see cref="ComplianceHarness.AssertProvenanceIsSubstantive"/>.
///
/// Distinct from <see cref="FixtureFailure"/> (a fixture's own hand-written assertion returned
/// false or threw): a <see cref="SubstanceFailure"/> is raised by the harness itself, independent
/// of what the fixture author chose to assert. It is the executable guard against the b72c1fa
/// regression class — an Affidavit that is structurally valid but substantively hollow (empty
/// <c>Fields</c>, <c>ProvenanceSource.Empty</c> everywhere) while every fixture assertion stays green.
///
/// <see cref="FieldName"/> names the offending field where the violation is field-scoped; for
/// affidavit-scoped or fixture-scoped violations it carries a marker such as <c>"(affidavit)"</c>.
/// </summary>
public sealed record SubstanceFailure(
    Type StrategyType,
    string FixtureCaseName,
    string FieldName,
    string Reason);
