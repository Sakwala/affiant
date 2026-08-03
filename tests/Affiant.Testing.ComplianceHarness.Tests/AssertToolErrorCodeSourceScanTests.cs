namespace Affiant.Testing.ComplianceHarness.Tests;

using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Area-3 P2 FIX-ROUND (finding 2): the LIVE half of the emission lock. Complements — does not
/// replace — <see cref="AssertToolErrorCodeRegistryParityTests.FrameworkRegistry_MatchesEveryCodeTheFrameworkActuallyEmits"/>.
///
/// <para>
/// <b>Why the parity assertion alone is not enough (division of labor).</b>
/// <c>ComplianceHarness.AssertToolErrorCodeRegistryParity</c>'s <c>emittedCodes</c> parameter is,
/// by design, a caller-supplied enumeration (see that method's own remarks — there is no honest
/// way to discover the live set by reflection). In
/// <c>AssertToolErrorCodeRegistryParityTests.FrameworkRegistry_...</c>, that list is hand-typed
/// FROM the very same <c>ToolErrorCodes</c> constants it is checked against — so it can only ever
/// detect an ORPHANED constant (one nothing emits), never a NEW bare-literal emission site that a
/// future change adds. Refuter A proved this by mutation: adding a rogue
/// <c>"RATE_LIMITED"</c> classification arm to <c>ToolErrorFilter.MapExceptionToToolError</c> that
/// no <c>ToolErrorCodes</c> constant declares failed nothing (306 relevant tests green) — the
/// hand-typed list is tautological with respect to new emissions.
/// </para>
///
/// <para>
/// <b>What this test does instead.</b> It reads the framework's own <c>src/</c> tree from disk
/// (repo-root discovered the same way <c>Affiant.Abstractions.Tests.Spec.DescriptorSpecSyncTests</c>
/// does — walk up from <c>AppContext.BaseDirectory</c> until <c>Affiant.slnx</c> is found — robust
/// to `dotnet test` from any directory, IDE runners, or CI with an arbitrary working directory) and
/// greps for the three concrete shapes every real <c>ToolError</c>-code emission site in this
/// codebase has taken:
/// </para>
/// <list type="bullet">
/// <item><description><b>Rule A</b> — <c>Code: "LITERAL"</c>: a bare literal passed as the named
/// <c>Code</c> argument to a <c>ToolError</c> construction.</description></item>
/// <item><description><b>Rule B</b> — <c>=&gt; ("LITERAL", true|false)</c>: the
/// <c>(code, retryable)</c> classification-tuple arm shape <c>MapExceptionToToolError</c> uses.
/// This is the rule that catches refuter A's exact mutation: the rogue arm never reaches a
/// <c>Code: "..."</c> call site directly (it flows through an intermediate <c>code</c> local first,
/// consumed later as <c>Code: code</c>) — Rule A alone would miss it.</description></item>
/// <item><description><b>Rule C</b> — <c>"code":"LITERAL"</c> (escaped or unescaped): a hand-rolled
/// JSON <c>ToolError</c>-shaped string literal bypassing the type entirely — the exact shape
/// <c>ManualToolInvoker</c>'s pre-fix <c>FUNCTION_NOT_FOUND</c> literal took.</description></item>
/// </list>
/// <para>
/// Scoped to <c>src/</c> only, never <c>tests/</c> (test fixtures legitimately construct
/// <c>ToolError</c>s with arbitrary literal codes to exercise serialization, parity checks, etc. —
/// see <c>ToolEnvelopePolymorphismTests</c>). Every match is a violation, full stop: a legitimate
/// new framework code must be declared as a <see cref="Affiant.Abstractions.Models.ToolErrorCodes"/>
/// constant and referenced by name, never inlined as a literal — even if its value happens to equal
/// an already-declared code's value (that would itself be a drift risk if the two ever diverge).
/// <see cref="ExemptRelativeFilePaths"/> is the documented escape hatch for a future legitimate
/// false positive; it is empty today (2026-08-03) — nothing has needed it yet, and it must never be
/// populated speculatively.
/// </para>
/// </summary>
public class AssertToolErrorCodeSourceScanTests
{
    /// <summary>
    /// Repo-relative (from <c>src/</c>), forward-slash paths exempted from this scan. Empty today —
    /// see class remarks. Add an entry only with an inline comment explaining the specific false
    /// positive it silences.
    /// </summary>
    private static readonly string[] ExemptRelativeFilePaths = [];

    // Requires the captured literal to look like a code (SCREAMING_SNAKE, starting with a letter) —
    // narrows the scan to the shapes real ToolError-code emission sites take in this codebase,
    // rather than flagging arbitrary quoted strings (which would false-positive on unrelated
    // literals like connection-string env var names — see this class's fix-round notes for the one
    // such literal found and deliberately NOT flagged: AffiantDbContextFactory's
    // "AFFIANT_EF_DESIGN_TIME_CONNECTION", which never appears in any of these three shapes).
    private const string CodePattern = "[A-Z][A-Z0-9_]*";

    private static readonly Regex NamedCodeArgLiteral =
        new($"Code:\\s*\"({CodePattern})\"", RegexOptions.Compiled);

    private static readonly Regex ClassificationTupleLiteral =
        new($"=>\\s*\\(\\s*\"({CodePattern})\"\\s*,\\s*(?:true|false)\\s*\\)", RegexOptions.Compiled);

    private static readonly Regex JsonCodeFieldLiteral =
        new($"\\\\?\"code\\\\?\"\\s*:\\s*\\\\?\"({CodePattern})\\\\?\"", RegexOptions.Compiled);

    // Walks up from the test assembly directory until the repo-root Affiant.slnx is found, then
    // returns the src/ tree adjacent to it. Robust to: dotnet test from any directory, IDE test
    // runners, CI with arbitrary working-directory. Mirrors
    // Affiant.Abstractions.Tests.Spec.DescriptorSpecSyncTests.ResolveSpecPath exactly (imitated
    // deliberately — that is this repo's established source-scanning-test pattern).
    private static string ResolveSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Affiant.slnx")))
                return Path.Combine(dir.FullName, "src");
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not find the Affiant.slnx anchor walking up from AppContext.BaseDirectory. " +
            "Source-scan test cannot locate src/. Check your working directory or CI configuration.");
    }

    private sealed record Violation(string RelativePath, int LineNumber, string LineText, string Literal, string Rule);

    [Fact]
    public void NoBareLiteralToolErrorCodeEmissionSitesExistInSrc()
    {
        var srcRoot = ResolveSrcRoot();
        Assert.True(Directory.Exists(srcRoot), $"Resolved src/ root does not exist: {srcRoot}");

        var exempt = new HashSet<string>(ExemptRelativeFilePaths, StringComparer.OrdinalIgnoreCase);

        var files = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // Sanity check on the scan itself: if this collapses to near-zero, the root resolution is
        // broken (wrong directory), not that the framework's src/ tree actually shrank that far —
        // fail loudly rather than silently "passing" an empty scan.
        Assert.True(files.Count > 10,
            $"Source scan found only {files.Count} .cs file(s) under {srcRoot} — the scan is " +
            "almost certainly broken (wrong root resolved via Affiant.slnx discovery), not that " +
            "src/ actually shrank to this size. Check AppContext.BaseDirectory / working directory.");

        var violations = new List<Violation>();

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(srcRoot, file).Replace('\\', '/');
            if (exempt.Contains(relativePath))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                CollectMatches(NamedCodeArgLiteral, "Rule A (Code: \"LITERAL\")", relativePath, i + 1, line, violations);
                CollectMatches(ClassificationTupleLiteral, "Rule B ((code, retryable) classification tuple arm)", relativePath, i + 1, line, violations);
                CollectMatches(JsonCodeFieldLiteral, "Rule C (hand-rolled JSON \"code\":\"LITERAL\")", relativePath, i + 1, line, violations);
            }
        }

        if (violations.Count > 0)
        {
            var message = string.Join(Environment.NewLine, violations.Select(v =>
                $"  src/{v.RelativePath}:{v.LineNumber} — bare literal \"{v.Literal}\" matches {v.Rule}: {v.LineText.Trim()}"));

            Assert.Fail(
                $"Found {violations.Count} bare-literal ToolError code emission site(s) in src/ " +
                "that escaped the Affiant.Abstractions.Models.ToolErrorCodes registry. Every " +
                "ToolError code must be a ToolErrorCodes.* constant reference, never a quoted " +
                "string literal:" + Environment.NewLine + message);
        }
    }

    private static void CollectMatches(
        Regex pattern, string ruleName, string relativePath, int lineNumber, string line,
        List<Violation> violations)
    {
        foreach (Match match in pattern.Matches(line))
        {
            violations.Add(new Violation(relativePath, lineNumber, line, match.Groups[1].Value, ruleName));
        }
    }
}
