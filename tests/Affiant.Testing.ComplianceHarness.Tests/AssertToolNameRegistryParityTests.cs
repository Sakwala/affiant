namespace Affiant.Testing.ComplianceHarness.Tests;

using Xunit;

/// <summary>
/// Tests for the opt-in <see cref="ComplianceHarness.AssertToolNameRegistryParity"/> (Area 2 P2,
/// generalizing <see cref="ComplianceHarness.AssertFieldSetParity"/> to the tool-name boundary).
/// </summary>
public class AssertToolNameRegistryParityTests
{
    private static class CleanToolNames
    {
        public const string SearchAircraft = "search_aircraft";
        public const string CreateWorkOrder = "create_work_order";
    }

    // --- Positive: exact bijection → passes ---

    [Fact]
    public void ExactBijection_Passes()
    {
        var result = ComplianceHarness.AssertToolNameRegistryParity(
            typeof(CleanToolNames), ["search_aircraft", "create_work_order"]);

        Assert.True(result.Passed);
        Assert.Empty(result.UndeclaredTools);
        Assert.Empty(result.OrphanConstants);
        Assert.Empty(result.AmbiguousConstants);
    }

    // --- Mutation: a tool exposed under a raw literal / default name not in ToolNames ---

    [Fact]
    public void RogueExposedTool_FailsWithPreciseMessage_NamingTheTool()
    {
        var result = ComplianceHarness.AssertToolNameRegistryParity(
            typeof(CleanToolNames), ["search_aircraft", "create_work_order", "rogue_tool"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.UndeclaredTools);
        Assert.Equal("rogue_tool", violation.Member);
        Assert.Contains("rogue_tool", violation.Reason);
        Assert.Contains("CleanToolNames", violation.Reason);
    }

    // --- Mutation: a ToolNames member whose value matches no exposed tool (orphan) ---

    [Fact]
    public void OrphanConstant_FailsWithPreciseMessage_NamingTheConstant()
    {
        var result = ComplianceHarness.AssertToolNameRegistryParity(
            typeof(CleanToolNames), ["search_aircraft"]); // CreateWorkOrder never exposed

        Assert.False(result.Passed);
        var violation = Assert.Single(result.OrphanConstants);
        Assert.Equal("CreateWorkOrder", violation.Member);
        Assert.Contains("CreateWorkOrder", violation.Reason);
        Assert.Contains("create_work_order", violation.Reason);
        Assert.Contains("orphaned", violation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // --- Mutation: two tools accidentally share one declared name (ambiguous) ---

    [Fact]
    public void AmbiguousConstant_FailsWithPreciseMessage_NamingTheConstant()
    {
        var result = ComplianceHarness.AssertToolNameRegistryParity(
            typeof(CleanToolNames), ["search_aircraft", "search_aircraft", "create_work_order"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.AmbiguousConstants);
        Assert.Equal("SearchAircraft", violation.Member);
        Assert.Contains("2 exposed", violation.Reason);
    }

    // --- Exemption: an exempted constant with zero matches does not fail ---

    [Fact]
    public void ExemptConstant_WithNoMatches_DoesNotFailOrphanCheck()
    {
        var result = ComplianceHarness.AssertToolNameRegistryParity(
            typeof(CleanToolNames), ["search_aircraft"], exemptConstants: ["CreateWorkOrder"]);

        Assert.True(result.Passed);
        Assert.Empty(result.OrphanConstants);
    }

    [Fact]
    public void ExemptConstant_StillFlaggedAsAmbiguous_IfExposedTwice()
    {
        // Exemption only silences the zero-matches direction, never the collision direction.
        var result = ComplianceHarness.AssertToolNameRegistryParity(
            typeof(CleanToolNames),
            ["search_aircraft", "create_work_order", "create_work_order"],
            exemptConstants: ["CreateWorkOrder"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.AmbiguousConstants);
        Assert.Equal("CreateWorkOrder", violation.Member);
    }

    [Fact]
    public void NullToolNamesType_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ComplianceHarness.AssertToolNameRegistryParity(null!, []));
    }

    [Fact]
    public void NullExposedToolNames_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ComplianceHarness.AssertToolNameRegistryParity(typeof(CleanToolNames), null!));
    }
}
