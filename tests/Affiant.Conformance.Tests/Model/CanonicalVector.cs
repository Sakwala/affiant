using System.Text.Json.Nodes;

namespace Affiant.Conformance.Tests.Model;

/// <summary>
/// One canonical-serialization byte vector (SR-1) — a different document shape from a fixture and
/// not run through the step machinery (<c>RUNNER.md</c> §9): an input Affidavit, the amendments
/// accepted on it, the decision they arrived on, and the exact bytes and digest those produce.
/// </summary>
internal sealed record CanonicalVector(
    string Id,
    IReadOnlyList<string> Rules,
    string Note,
    JsonObject Input,
    JsonObject? Amendments,
    JsonObject? ReviewerAct,
    string ExpectedBytesUtf8,
    string ExpectedSha256,
    string SourcePath);

/// <summary>One row of <c>fixtures/MANIFEST.json</c>, section <c>"conformance"</c> — the index a driver runs.</summary>
internal sealed record ManifestEntry(
    string Id,
    string File,
    IReadOnlyList<string> Rules,
    string Set,
    OracleEntry? Oracle);

/// <summary>The negative oracle for one fixture: the release it MUST fail against and the shipped defect it refutes.</summary>
internal sealed record OracleEntry(IReadOnlyList<string> MustFailOn, string Defect);
