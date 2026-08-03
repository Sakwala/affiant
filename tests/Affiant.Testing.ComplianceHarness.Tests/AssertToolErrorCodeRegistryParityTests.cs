namespace Affiant.Testing.ComplianceHarness.Tests;

using Xunit;

/// <summary>
/// Tests for the opt-in <see cref="ComplianceHarness.AssertToolErrorCodeRegistryParity"/> (area-3
/// P2 ruling 4, generalizing <see cref="ComplianceHarness.AssertFabricKeyParity"/> to
/// <c>ToolError.Code</c> values).
/// </summary>
public class AssertToolErrorCodeRegistryParityTests
{
    private static class CleanToolErrorCodes
    {
        public const string DbTimeout = "DB_TIMEOUT";
        public const string ValidationFailed = "VALIDATION_FAILED";
    }

    // --- Positive: every declared constant is emitted, every emitted code is declared → passes ---

    [Fact]
    public void ExactBijection_Passes()
    {
        var result = ComplianceHarness.AssertToolErrorCodeRegistryParity(
            typeof(CleanToolErrorCodes), ["DB_TIMEOUT", "VALIDATION_FAILED"]);

        Assert.True(result.Passed);
        Assert.Empty(result.OrphanConstants);
        Assert.Empty(result.UndeclaredCodes);
    }

    // --- Mutation: a bare-literal emitted code that escaped the registry ---

    [Fact]
    public void RogueEmittedCode_FailsWithPreciseMessage_NamingTheCode()
    {
        var result = ComplianceHarness.AssertToolErrorCodeRegistryParity(
            typeof(CleanToolErrorCodes), ["DB_TIMEOUT", "VALIDATION_FAILED", "TIMEOUT"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.UndeclaredCodes);
        Assert.Equal("TIMEOUT", violation.Member);
        Assert.Contains("TIMEOUT", violation.Reason);
        Assert.Contains("CleanToolErrorCodes", violation.Reason);
    }

    // --- Mutation: a declared constant no emission site actually produces (orphan) ---

    [Fact]
    public void OrphanConstant_FailsWithPreciseMessage_NamingTheConstant()
    {
        var result = ComplianceHarness.AssertToolErrorCodeRegistryParity(
            typeof(CleanToolErrorCodes), ["DB_TIMEOUT"]); // ValidationFailed never emitted

        Assert.False(result.Passed);
        var violation = Assert.Single(result.OrphanConstants);
        Assert.Equal("ValidationFailed", violation.Member);
        Assert.Contains("ValidationFailed", violation.Reason);
        Assert.Contains("orphaned", violation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // --- Exemption: an exempted constant with no emitted match does not fail ---

    [Fact]
    public void ExemptConstant_WithNoEmittedMatch_DoesNotFailOrphanCheck()
    {
        var result = ComplianceHarness.AssertToolErrorCodeRegistryParity(
            typeof(CleanToolErrorCodes), ["DB_TIMEOUT"], exemptConstants: ["ValidationFailed"]);

        Assert.True(result.Passed);
        Assert.Empty(result.OrphanConstants);
    }

    // --- Duplicate emitted codes are harmless (deduplicated) ---

    [Fact]
    public void DuplicateEmittedCodes_DoNotProduceDuplicateFindings()
    {
        var result = ComplianceHarness.AssertToolErrorCodeRegistryParity(
            typeof(CleanToolErrorCodes), ["DB_TIMEOUT", "DB_TIMEOUT", "VALIDATION_FAILED"]);

        Assert.True(result.Passed);
    }

    [Fact]
    public void NullToolErrorCodesType_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ComplianceHarness.AssertToolErrorCodeRegistryParity(null!, []));
    }

    [Fact]
    public void NullEmittedCodes_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ComplianceHarness.AssertToolErrorCodeRegistryParity(typeof(CleanToolErrorCodes), null!));
    }

    // --- Framework self-check: the real Affiant.Abstractions.Models.ToolErrorCodes registry ---
    // against every code actually emitted by the framework today (enumerated by hand from the
    // code, per area-3 P2 ruling 4 — not trusting the position paper's estimate of "4"). This is
    // the self-parity lock: adding a rogue emission or deleting a declared constant must fail it
    // (exercised as a real self-mutation against the framework source in the FIX report, not just
    // this in-memory test — this test is the harness-shape lock that the self-mutation script
    // drives against).

    [Fact]
    public void FrameworkRegistry_MatchesEveryCodeTheFrameworkActuallyEmits()
    {
        // Enumerated from Affiant.Core.Filters.ToolErrorFilter.MapExceptionToToolError (4),
        // Affiant.Core.Filters.ReviewGateFilter's REVIEW_FILING_FAILED (P1a), and
        // Affiant.SemanticKernel.Connectors.ManualToolInvoker's hand-written FUNCTION_NOT_FOUND
        // JSON literal (still enumerable here even though the literal itself is out of scope for
        // this wave — see ToolErrorCodes' own remarks).
        string[] emittedByFramework =
        [
            Affiant.Abstractions.Models.ToolErrorCodes.DbTimeout,
            Affiant.Abstractions.Models.ToolErrorCodes.UpstreamUnavailable,
            Affiant.Abstractions.Models.ToolErrorCodes.ValidationFailed,
            Affiant.Abstractions.Models.ToolErrorCodes.Unknown,
            Affiant.Abstractions.Models.ToolErrorCodes.ReviewFilingFailed,
            Affiant.Abstractions.Models.ToolErrorCodes.FunctionNotFound,
        ];

        var result = ComplianceHarness.AssertToolErrorCodeRegistryParity(
            typeof(Affiant.Abstractions.Models.ToolErrorCodes), emittedByFramework);

        Assert.True(result.Passed);
        Assert.Empty(result.OrphanConstants);
        Assert.Empty(result.UndeclaredCodes);
    }
}
