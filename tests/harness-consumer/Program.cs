using Affiant.Testing.ComplianceHarness;

// A consumer of the PACKED compliance harness, run the way an adopter runs it: the only Affiant
// reference is the NuGet package, and the rulebook is a directory of this project's own choosing
// that is NOT beside the assembly. What it proves is that a conformance run is self-contained —
// every fixture, both schemas and the telemetry registry come from the root the caller named.
//
//   dotnet run --project tests/harness-consumer -- <protocol-root>
//
// Exit code 0 when every fixture in that rulebook passes; 1 otherwise, naming what failed.

var root = args.Length > 0
    ? args[0]
    : throw new ArgumentException("Usage: harness-consumer <protocol-root>");

Console.WriteLine($"harness-consumer: protocolRoot={Path.GetFullPath(root)}");
Console.WriteLine($"harness-consumer: assembly={AppContext.BaseDirectory}");
Console.WriteLine(
    "harness-consumer: a rulebook beside the assembly? "
    + Directory.Exists(Path.Combine(AppContext.BaseDirectory, "protocol")));

var report = ConformanceSuite.Run(protocolRoot: root, writeRunTo: null);

Console.WriteLine(
    $"harness-consumer: outcomes={report.Outcomes.Count} passed={report.Passed} "
    + $"failing=[{string.Join(", ", report.FailingIds)}]");

var summary = report.Document["summary"]!;
Console.WriteLine(
    $"harness-consumer: {summary["passed"]} passed, {summary["failed"]} failed, "
    + $"{summary["errored"]} errored of {summary["total"]}, "
    + $"protocol {report.Document["protocolTag"]}");

if (report.Outcomes.Count == 0)
{
    Console.Error.WriteLine("harness-consumer: the run produced no outcomes at all.");
    return 1;
}

if (!report.Passed)
{
    foreach (var outcome in report.Outcomes.Where(o => o.Verdict != "pass"))
    {
        Console.Error.WriteLine($"  {outcome.Id}: {outcome.Verdict} {outcome.Reason}");
    }

    return 1;
}

return 0;
