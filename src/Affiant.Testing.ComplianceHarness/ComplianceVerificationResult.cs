namespace Affiant.Testing.ComplianceHarness;

/// <summary>
/// The outcome of <see cref="ComplianceHarness.Verify"/>.
///
/// <see cref="Passed"/> is <c>true</c> only when all three failure lists are empty. Each list is
/// an orthogonal contract:
/// <list type="bullet">
/// <item><see cref="MissingFixtures"/> — a write strategy with no paired compliance fixture (discoverability).</item>
/// <item><see cref="FixtureFailures"/> — a fixture's own hand-written assertion returned false or threw.</item>
/// <item><see cref="SubstanceFailures"/> — the harness's own substantive-provenance gate found a hollow
/// Affidavit (the b72c1fa regression class), independent of the fixture's assertion.</item>
/// </list>
/// </summary>
public sealed record ComplianceVerificationResult(
    bool Passed,
    IReadOnlyList<MissingFixture> MissingFixtures,
    IReadOnlyList<FixtureFailure> FixtureFailures,
    IReadOnlyList<SubstanceFailure> SubstanceFailures);
