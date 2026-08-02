namespace Affiant.Testing.ComplianceHarness;

/// <summary>
/// A single finding from <see cref="ComplianceHarness.AssertToolNameRegistryParity"/> or
/// <see cref="ComplianceHarness.AssertFabricKeyParity"/> — naming either a declared constant
/// member or a live (exposed/consumed) name, whichever side of the parity check the violation
/// concerns. Shared across both checks rather than duplicated per-check the way
/// <see cref="FieldParityViolation"/> is not reused here, since neither check needs the
/// errors/warnings split <see cref="ComplianceHarness.AssertFieldSetParity"/> has.
/// </summary>
public sealed record ParityViolation(string Member, string Reason);
