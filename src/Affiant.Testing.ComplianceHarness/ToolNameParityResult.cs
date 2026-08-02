namespace Affiant.Testing.ComplianceHarness;

/// <summary>
/// The outcome of <see cref="ComplianceHarness.AssertToolNameRegistryParity"/>.
/// <see cref="Passed"/> is <c>true</c> only when all three lists are empty — unlike
/// <see cref="FieldSetParityResult"/>, there is no warning-only category here: both directions of
/// the bijection are hard errors.
/// </summary>
public sealed record ToolNameParityResult(
    bool Passed,
    IReadOnlyList<ParityViolation> UndeclaredTools,
    IReadOnlyList<ParityViolation> OrphanConstants,
    IReadOnlyList<ParityViolation> AmbiguousConstants);
