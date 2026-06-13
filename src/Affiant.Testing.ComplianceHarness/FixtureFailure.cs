namespace Affiant.Testing.ComplianceHarness;

public sealed record FixtureFailure(
    Type StrategyType,
    string FixtureCaseName,
    string Reason);
