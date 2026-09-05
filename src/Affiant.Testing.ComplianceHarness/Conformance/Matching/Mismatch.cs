using System.Text.Json.Nodes;

namespace Affiant.Testing.ComplianceHarness.Conformance.Matching;

/// <summary>
/// One stated fact that did not hold, at the dotted path it was found at — <c>entry.status</c>,
/// <c>card.fields[1].kind</c> — with what the fixture said and what the framework did.
/// </summary>
/// <remarks>
/// The runner never throws for a failed expectation: it returns every failure it found. A runner
/// that stopped at the first mismatch could tell you a fixture failed; it could not produce the
/// list of everything an implementation does not pass, which is the document a parity manifest is
/// derived from (<c>RUNNER.md</c> §6).
/// </remarks>
internal sealed record Mismatch(string At, JsonNode? Expected, JsonNode? Actual)
{
    /// <summary>A mismatch whose two sides are best said in words — a missing clause, an absent port.</summary>
    public static Mismatch Said(string at, string expected, string actual) =>
        new(at, JsonValue.Create(expected), JsonValue.Create(actual));
}
