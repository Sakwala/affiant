namespace Affiant.Testing.ComplianceHarness;

/// <summary>
/// The outcome of <see cref="ComplianceHarness.AssertFabricKeyParity"/>.
/// <see cref="Passed"/> is <c>true</c> only when both lists are empty — unlike
/// <see cref="FieldSetParityResult"/>, there is no warning-only category here: a fabric key must
/// be declared to be used, and a declared constant must be used to stay in the registry.
/// </summary>
public sealed record FabricKeyParityResult(
    bool Passed,
    IReadOnlyList<ParityViolation> OrphanConstants,
    IReadOnlyList<ParityViolation> UndeclaredKeys);
