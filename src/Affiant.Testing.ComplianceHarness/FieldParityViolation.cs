namespace Affiant.Testing.ComplianceHarness;

/// <summary>
/// A single finding from <see cref="ComplianceHarness.AssertFieldSetParity"/> — either an
/// <see cref="FieldSetParityResult.Errors"/> entry (a card field the write path never consumes)
/// or a <see cref="FieldSetParityResult.Warnings"/> entry (a consumed name the strategy never
/// declares).
/// </summary>
public sealed record FieldParityViolation(string FieldName, string Reason);
