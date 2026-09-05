using System.Text.Json.Nodes;
using Affiant.Testing.ComplianceHarness.Conformance.Reporting;

namespace Affiant.Testing.ComplianceHarness;

/// <summary>
/// Runs the protocol's own conformance suite — every declarative fixture and every canonical byte
/// vector — against the shipped packages, and reports what it found.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this ships.</b> A conformance result is only worth what produced it. The framework's own
/// runs and a host's runs go through this same code, so an adopter checking a claim about the
/// framework — or checking their own gate wiring against the rulebook before they depend on it —
/// gets the report the framework's release notes are derived from, not a re-implementation of the
/// runner living beside somebody's test.
/// </para>
/// <para>
/// <b>What the caller supplies.</b> The vendored rulebook: the directory holding <c>fixtures/</c>,
/// <c>fixture.schema.json</c> and <c>canonical-vector.schema.json</c> as
/// <c>affiant-protocol</c> publishes them. Vendoring is the caller's, deliberately — the suite a
/// run measures against has to be a document a reader can check, pinned in the caller's own
/// repository rather than fetched at run time. The framework's copy, and the script that vendors and
/// verifies it, are in <c>conformance/</c> of the Affiant repository.
/// </para>
/// <para>
/// <b>The root is the whole of it.</b> The fixtures, both schemas and the telemetry registry are
/// read from it and from nowhere else: there is no copy beside the assembly, no ambient default and
/// no fallback. A caller's fixtures held against a different rulebook's schema would be a
/// measurement nobody could interpret, and the shape that produced one — a package that quietly
/// read its own copy — is the shape this API exists to make impossible.
/// </para>
/// <para>
/// Every fixture is validated against the rulebook's schema before it runs, so a malformed document
/// is an error and never a pass; a fixture the run cannot execute at all is an error too, and an
/// error counts against the implementation exactly like a failure.
/// </para>
/// </remarks>
public static class ConformanceSuite
{
    /// <summary>Runs the suite at <paramref name="protocolRoot"/> and returns the report.</summary>
    /// <param name="protocolRoot">
    /// The vendored rulebook's root — the directory holding <c>fixtures/</c>,
    /// <c>fixture.schema.json</c> and <c>canonical-vector.schema.json</c>. Every file the run reads
    /// comes from here and from nowhere else; a root missing one of them throws before a single
    /// fixture is executed, naming the file and what it is for.
    /// </param>
    /// <param name="writeRunTo">
    /// A directory to write the run document into, named for the version it measured, or null to
    /// return it without writing. A run that only printed to a terminal could tell a reader that
    /// something failed without producing the list a parity manifest is derived from.
    /// </param>
    public static ConformanceReport Run(string protocolRoot, string? writeRunTo = null)
    {
        var run = ConformanceRun.Execute(protocolRoot, writeRunTo);
        return new ConformanceReport(
            run.Document,
            [.. run.Results.Select(r => new ConformanceOutcome(r.Id, r.Outcome, r.Reason))],
            run.WrittenTo);
    }
}

/// <summary>What one run of the conformance suite found.</summary>
/// <param name="Document">
/// The run, as the rulebook's <c>results.schema.json</c> describes it: what was measured, which
/// version and commit of the implementation, the protocol ref, and every fixture's outcome with the
/// diffs that produced it. This is the evidence a parity manifest rests on.
/// </param>
/// <param name="Outcomes">One entry per fixture the rulebook's index lists, including the ones that passed.</param>
/// <param name="WrittenTo">Where the document was written, or null when the caller asked for none.</param>
public sealed record ConformanceReport(
    JsonObject Document,
    IReadOnlyList<ConformanceOutcome> Outcomes,
    string? WrittenTo)
{
    /// <summary>The ids a parity manifest must list: every fixture that failed or errored.</summary>
    public IReadOnlyList<string> FailingIds =>
        [.. Outcomes.Where(o => o.Verdict is "fail" or "error").Select(o => o.Id)];

    /// <summary>Whether every fixture in the suite passed.</summary>
    public bool Passed => FailingIds.Count == 0;
}

/// <summary>One fixture's outcome.</summary>
/// <param name="Id">The fixture's stable id, as the rulebook names it.</param>
/// <param name="Verdict"><c>pass</c>, <c>fail</c> or <c>error</c>.</param>
/// <param name="Reason">Why a fixture errored, or why a step had no counterpart; null on a pass.</param>
public sealed record ConformanceOutcome(string Id, string Verdict, string? Reason);
