namespace Affiant.Testing.ComplianceHarness.Tests;

using Xunit;

/// <summary>
/// Tests for the opt-in <see cref="ComplianceHarness.AssertFabricKeyParity"/> (Area 2 P2,
/// generalizing <see cref="ComplianceHarness.AssertFieldSetParity"/> to fabric keys).
/// </summary>
public class AssertFabricKeyParityTests
{
    private static class CleanFabricKeys
    {
        public const string Aircraft = "Aircraft";
        public const string Employee = "Employee";
    }

    // --- Positive: every declared constant is live, every live key is declared → passes ---

    [Fact]
    public void ExactBijection_Passes()
    {
        var result = ComplianceHarness.AssertFabricKeyParity(
            typeof(CleanFabricKeys), ["Aircraft", "Employee"]);

        Assert.True(result.Passed);
        Assert.Empty(result.OrphanConstants);
        Assert.Empty(result.UndeclaredKeys);
    }

    // --- Mutation: a bare-literal fabric key that escaped the registry ---

    [Fact]
    public void RogueLiveKey_FailsWithPreciseMessage_NamingTheKey()
    {
        var result = ComplianceHarness.AssertFabricKeyParity(
            typeof(CleanFabricKeys), ["Aircraft", "Employee", "WorkOrder"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.UndeclaredKeys);
        Assert.Equal("WorkOrder", violation.Member);
        Assert.Contains("WorkOrder", violation.Reason);
        Assert.Contains("CleanFabricKeys", violation.Reason);
    }

    // --- Mutation: a declared constant no live call site actually uses (orphan) ---

    [Fact]
    public void OrphanConstant_FailsWithPreciseMessage_NamingTheConstant()
    {
        var result = ComplianceHarness.AssertFabricKeyParity(
            typeof(CleanFabricKeys), ["Aircraft"]); // Employee never used

        Assert.False(result.Passed);
        var violation = Assert.Single(result.OrphanConstants);
        Assert.Equal("Employee", violation.Member);
        Assert.Contains("Employee", violation.Reason);
        Assert.Contains("orphaned", violation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // --- Exemption: an exempted constant with no live match does not fail ---

    [Fact]
    public void ExemptConstant_WithNoLiveMatch_DoesNotFailOrphanCheck()
    {
        var result = ComplianceHarness.AssertFabricKeyParity(
            typeof(CleanFabricKeys), ["Aircraft"], exemptConstants: ["Employee"]);

        Assert.True(result.Passed);
        Assert.Empty(result.OrphanConstants);
    }

    // --- Duplicate live keys are harmless (deduplicated) ---

    [Fact]
    public void DuplicateLiveKeys_DoNotProduceDuplicateFindings()
    {
        var result = ComplianceHarness.AssertFabricKeyParity(
            typeof(CleanFabricKeys), ["Aircraft", "Aircraft", "Employee"]);

        Assert.True(result.Passed);
    }

    [Fact]
    public void NullFabricKeysType_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ComplianceHarness.AssertFabricKeyParity(null!, []));
    }

    [Fact]
    public void NullLiveKeys_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ComplianceHarness.AssertFabricKeyParity(typeof(CleanFabricKeys), null!));
    }
}
