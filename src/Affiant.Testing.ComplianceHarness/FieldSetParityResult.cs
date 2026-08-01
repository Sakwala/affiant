namespace Affiant.Testing.ComplianceHarness;

/// <summary>
/// The outcome of <see cref="ComplianceHarness.AssertFieldSetParity"/>.
/// <see cref="Passed"/> is <c>true</c> only when <see cref="Errors"/> is empty —
/// <see cref="Warnings"/> never affects <see cref="Passed"/>.
/// </summary>
public sealed record FieldSetParityResult(
    bool Passed,
    IReadOnlyList<FieldParityViolation> Errors,
    IReadOnlyList<FieldParityViolation> Warnings);
