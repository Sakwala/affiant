namespace Affiant.Testing.ComplianceHarness;

/// <summary>
/// The outcome of <see cref="ComplianceHarness.AssertToolErrorCodeRegistryParity"/>.
/// <see cref="Passed"/> is <c>true</c> only when both lists are empty — unlike
/// <see cref="FieldSetParityResult"/>, there is no warning-only category here: an emitted
/// <c>ToolError.Code</c> must be declared to be emitted, and a declared constant must be emitted
/// somewhere to stay in the registry (mirrors <see cref="FabricKeyParityResult"/>'s shape).
/// </summary>
public sealed record ToolErrorCodeParityResult(
    bool Passed,
    IReadOnlyList<ParityViolation> OrphanConstants,
    IReadOnlyList<ParityViolation> UndeclaredCodes);
