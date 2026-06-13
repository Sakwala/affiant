namespace Affiant.Testing.ComplianceHarness;

public sealed record ComplianceVerificationResult(
    bool Passed,
    IReadOnlyList<MissingFixture> MissingFixtures,
    IReadOnlyList<FixtureFailure> FixtureFailures);
