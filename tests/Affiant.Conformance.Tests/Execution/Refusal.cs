namespace Affiant.Conformance.Tests.Execution;

/// <summary>A refusal, as the format asks for it: a stable code and a human-readable reason.</summary>
internal sealed record Refusal(string Code, string Message);

/// <summary>
/// The refusal codes a fixture pins, and what in <c>1.0.0-beta.1</c> the driver reads as each one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The framework has no refusal codes.</b> Its only declared error codes are the six in
/// <c>ToolErrorCodes</c> (<c>DB_TIMEOUT</c>, <c>FUNCTION_NOT_FOUND</c>,
/// <c>REVIEW_FILING_FAILED</c>, <c>UNKNOWN</c>, <c>UPSTREAM_UNAVAILABLE</c>,
/// <c>VALIDATION_FAILED</c>), none of which is about the gate's own refusals; everything else is an
/// <c>InvalidOperationException</c> with prose, or a <c>ReviewOutcome</c> that has to be read as a
/// refusal. The driver therefore maps the observable behaviour to the code the fixture pins, and
/// the table is written down here because a mapping nobody can read is a mapping nobody can check.
/// </para>
/// <list type="table">
/// <listheader><term>Fixture code</term><description>What the driver reads as that code</description></listheader>
/// <item><term><c>decision-expired</c></term><description><c>HandleDecisionAsync</c> returns <c>ReviewOutcome.Expired</c> for a row whose deadline had lapsed — the one branch that reads the clock and refuses.</description></item>
/// <item><term><c>decision-not-pending</c></term><description><c>HandleDecisionAsync</c> returns an outcome for a row that was already <c>Approved</c> or <c>Rejected</c> (the CAS affected no row), or <c>ResubmitAsync</c> throws "is {status}, expected Expired".</description></item>
/// <item><term><c>entry-not-found</c></term><description><c>HandleDecisionAsync</c> returns <c>(null, null)</c> for an id the store does not hold, or <c>ResubmitAsync</c> throws "was not found".</description></item>
/// <item><term><c>wireup-invalid</c></term><description><c>AffiantStartupException</c>, or an <c>InvalidOperationException</c> raised while the gate is being built.</description></item>
/// <item><term><c>coverage-refused</c></term><description><b>Unreachable from the shipped core.</b> The only coverage refusal in this release is <c>HostedToolAudit</c>, an <c>internal</c> class inside the two adapter packages, raised at <c>WithAffiant</c> wire-up. Nothing in <c>Affiant.Core</c>, <c>Affiant.Docket</c> or <c>Affiant.Policies</c> has a coverage concept.</description></item>
/// <item><term><c>decision-unauthorized</c></term><description><b>No counterpart.</b> The decision path consults no authorization port, so no act can produce this code (AZ-2).</description></item>
/// <item><term><c>substance-refused</c></term><description><b>No counterpart at run time.</b> Substance is checked by the compliance harness at test time; the runtime files what it is given (GT-3).</description></item>
/// <item><term><c>execution-already-recorded</c></term><description><b>No counterpart.</b> There is no execution state to have recorded (DK-1).</description></item>
/// </list>
/// </remarks>
internal static class RefusalCodes
{
    public const string DecisionExpired = "decision-expired";
    public const string DecisionNotPending = "decision-not-pending";
    public const string DecisionUnauthorized = "decision-unauthorized";
    public const string EntryNotFound = "entry-not-found";
    public const string WireUpInvalid = "wireup-invalid";
    public const string CoverageRefused = "coverage-refused";
    public const string SubstanceRefused = "substance-refused";
    public const string ExecutionAlreadyRecorded = "execution-already-recorded";

    /// <summary>Reads an exception the framework threw as the refusal a fixture would pin, or leaves it to escape.</summary>
    public static Refusal? FromException(Exception exception) => exception switch
    {
        Affiant.Abstractions.Exceptions.AffiantStartupException => new Refusal(WireUpInvalid, exception.Message),
        InvalidOperationException when exception.Message.Contains("was not found", StringComparison.Ordinal)
            => new Refusal(EntryNotFound, exception.Message),
        InvalidOperationException when exception.Message.Contains("expected Expired", StringComparison.Ordinal)
            => new Refusal(DecisionNotPending, exception.Message),
        InvalidOperationException when exception.Message.Contains("already resubmitted", StringComparison.Ordinal)
            => new Refusal(DecisionNotPending, exception.Message),
        InvalidOperationException when exception.Message.Contains("uncovered", StringComparison.Ordinal)
            => new Refusal(CoverageRefused, exception.Message),
        _ => null,
    };
}
