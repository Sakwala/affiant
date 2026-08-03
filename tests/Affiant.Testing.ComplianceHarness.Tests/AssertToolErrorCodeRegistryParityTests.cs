namespace Affiant.Testing.ComplianceHarness.Tests;

using Xunit;

/// <summary>
/// Tests for the opt-in <see cref="ComplianceHarness.AssertToolErrorCodeRegistryParity"/> (area-3
/// P2 ruling 4, generalizing <see cref="ComplianceHarness.AssertFabricKeyParity"/> to
/// <c>ToolError.Code</c> values).
///
/// <para>
/// <b>Division of labor (area-3 P2 fix round, finding 2).</b> This parity assertion's
/// <c>emittedCodes</c> parameter is caller-supplied by design (see
/// <see cref="ComplianceHarness.AssertToolErrorCodeRegistryParity"/>'s own remarks) — so it can
/// only ever catch an ORPHANED constant (a declared code nothing emits). It has ZERO power to catch
/// a NEW bare-literal emission site: <see cref="FrameworkRegistry_MatchesEveryCodeTheFrameworkActuallyEmits"/>
/// below hand-types its "emitted" list FROM the very same <c>ToolErrorCodes</c> constants it checks
/// against, so it is tautological with respect to anything not already on that list. Refuter A
/// proved this by mutation: a rogue <c>"RATE_LIMITED"</c> classification arm added to
/// <c>ToolErrorFilter.MapExceptionToToolError</c> failed nothing here (306 relevant tests green).
/// Catching that class of drift is <c>AssertToolErrorCodeSourceScanTests</c>' job — it reads
/// <c>src/</c> from disk and greps for bare-literal emission shapes directly, with no caller-supplied
/// list to go stale. Keep both: this test still legitimately catches orphans (a declared constant
/// nothing emits any more, e.g. after a rename) that the source scan cannot.
/// </para>
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
    // code, per area-3 P2 ruling 4 — not trusting the position paper's estimate of "4"). This
    // catches an ORPHAN (a declared constant nothing emits any more) — it does NOT catch a rogue
    // NEW emission, because "emittedByFramework" below is hand-typed FROM these same constants
    // (area-3 P2 fix round, finding 2 — see this class's own remarks for the division of labor with
    // AssertToolErrorCodeSourceScanTests, which IS the self-mutation-proof lock for new emissions;
    // proven by mutation in the FIX report: a rogue "RATE_LIMITED" arm added to
    // ToolErrorFilter.MapExceptionToToolError does NOT fail this test, by construction).

    [Fact]
    public void FrameworkRegistry_MatchesEveryCodeTheFrameworkActuallyEmits()
    {
        // Enumerated from Affiant.Core.Filters.ToolErrorFilter.MapExceptionToToolError (4),
        // Affiant.Core.Filters.ReviewGateFilter's REVIEW_FILING_FAILED (P1a), and
        // Affiant.SemanticKernel.Connectors.ManualToolInvoker's FUNCTION_NOT_FOUND (now built
        // through the real ToolError type + this constant, area-3 P2 fix round — see ToolErrorCodes'
        // own remarks).
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
