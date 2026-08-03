namespace Affiant.Abstractions.Models;

/// <summary>
/// Typed registry of every <see cref="ToolError.Code"/> value the FRAMEWORK itself emits
/// (area-3 P2 ruling 4 — the Area-2 harness treatment applied to <c>ToolError.Code</c>, which
/// until now was a bare string with no shared constants class, no registry, and no contract test).
///
/// <para><b>Scope — framework-emitted codes only.</b> A host's own domain codes (e.g. HRPortal's
/// <c>EMPLOYEE_NOT_FOUND</c>/<c>INVALID_DATES</c>/etc.) are NOT declared here — hosts register their
/// own <c>ToolErrorCodes</c>-style class and call
/// <c>Affiant.Testing.ComplianceHarness.ComplianceHarness.AssertToolErrorCodeRegistryParity</c>
/// against it, the same additive pattern <c>AssertToolNameRegistryParity</c>/
/// <c>AssertFabricKeyParity</c> already establish. Host-side adoption of a host's OWN domain codes
/// is still deferred to the Area-3 closing wave (declaring this registry must not break any host at
/// the next pin bump before they choose to adopt it).</para>
///
/// <para><b>Framework-side adoption is complete as of the area-3 P2 fix round (2026-08-03).</b>
/// <c>ManualToolInvoker</c>'s hand-written <see cref="FunctionNotFound"/> JSON literal was corrected
/// scope: it is a framework code, not host-side adoption — grouping it with host adoption in the P2
/// wave that introduced this registry was a scoping error, corrected here.
/// <c>ManualToolInvoker.CaptureAndInvokeAsync</c> now builds its not-found payload through the real
/// <see cref="ToolError"/> type consuming this constant. The mismatched bare-<c>"TIMEOUT"</c>/
/// <c>"DB_CONN_TIMEOUT"</c> test assertions in
/// <c>Affiant.Core.Tests.Primitives.ToolEnvelopePolymorphismTests</c> were also fixed to assert
/// <see cref="DbTimeout"/>. A source-scan lock
/// (<c>Affiant.Testing.ComplianceHarness.Tests.AssertToolErrorCodeSourceScanTests</c>) now catches
/// any FUTURE bare-literal framework emission site the hand-typed
/// <c>AssertToolErrorCodeRegistryParityTests.FrameworkRegistry_...</c> self-check cannot (that test
/// supplies its "emitted" list from these same constants, so it can only catch orphans — see this
/// registry's own division-of-labor note there).</para>
///
/// <para><b>Enumerated from the code, not the position paper's count.</b> The position paper
/// (<c>docs/architecture-review/area-3-tool-calling-reliability.md</c>, V6) estimated "4" framework
/// codes; grepping <c>ToolErrorFilter.MapExceptionToToolError</c> plus the P1a addition
/// (<c>REVIEW_FILING_FAILED</c>) plus <c>ManualToolInvoker</c>'s literal yields six.</para>
/// </summary>
public static class ToolErrorCodes
{
    /// <summary>A DB write/read timed out or an <c>DbUpdateException</c> occurred. Retryable.</summary>
    public const string DbTimeout = "DB_TIMEOUT";

    /// <summary>An upstream HTTP dependency returned 503. Retryable.</summary>
    public const string UpstreamUnavailable = "UPSTREAM_UNAVAILABLE";

    /// <summary>An <see cref="ArgumentException"/>/<see cref="InvalidOperationException"/> from the
    /// tool call. Non-retryable — the arguments themselves are the problem.</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>Catch-all for any exception type <c>ToolErrorFilter</c> does not specifically
    /// classify. Non-retryable.</summary>
    public const string Unknown = "UNKNOWN";

    /// <summary>
    /// <c>ReviewGateFilter</c>'s call to <c>ReviewGate.FileReviewAsync</c> threw (P1a, affiant#22 /
    /// FV-9) — the WriteProposal was not filed and was not queued for review. Non-retryable (a
    /// second attempt would risk double-filing once the underlying cause is transient and the model
    /// simply retries the whole tool call instead).
    /// </summary>
    public const string ReviewFilingFailed = "REVIEW_FILING_FAILED";

    /// <summary>
    /// <c>ManualToolInvoker.CaptureAndInvokeAsync</c> could not resolve the named function in the
    /// kernel's plugin collection. Emitted as a hand-written JSON string literal, not through this
    /// constant or the <see cref="ToolError"/> record's serializer — see this type's remarks;
    /// wiring <c>ManualToolInvoker</c> to consume this constant is explicitly out of scope for the
    /// P2 wave that introduced this registry and rides the Area-3 closing wave instead.
    /// </summary>
    public const string FunctionNotFound = "FUNCTION_NOT_FOUND";
}
