namespace Affiant.Testing.ComplianceHarness;

public sealed record MissingFixture(
    Type StrategyType,
    string FunctionName);
