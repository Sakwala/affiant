using System.Text.Json.Nodes;
using Affiant.Abstractions.Models;
using Affiant.Conformance.Tests.Matching;
using Affiant.Conformance.Tests.Model;
using Affiant.Conformance.Tests.Ports;

namespace Affiant.Conformance.Tests.Execution;

/// <summary>One fixture, run end to end: the wiring, the acts, and every stated fact checked.</summary>
/// <remarks>
/// The runner never throws for a failed expectation. It returns every failure it found, each with
/// the path and the two values, because a runner that stopped at the first mismatch could tell you
/// a fixture failed but could not produce the list a parity manifest is derived from
/// (<c>RUNNER.md</c> §6). A programming error in the driver or a port is a different thing and is
/// allowed to propagate: swallowing it would hide a broken driver behind a red test.
/// </remarks>
internal sealed class FixtureRunner
{
    /// <summary>What running one fixture produced.</summary>
    internal sealed record Outcome(string Verdict, IReadOnlyList<Mismatch> Diff, string? Reason);

    public static async Task<Outcome> RunAsync(Fixture fixture, CancellationToken ct)
    {
        GateHarness harness;
        try
        {
            harness = GateHarness.Build(fixture.Given.Gate, fixture.Given.Clock);
        }
        catch (Exception exception) when (RefusalCodes.FromException(exception) is not null)
        {
            // CV-1: a refusal raised while the gate is being BUILT is reported through the same
            // expect.error clause a step's refusal is, and nothing after it runs. On that path only
            // error, telemetry, telemetryAbsent and store are answerable.
            return WireUpRefused(fixture, RefusalCodes.FromException(exception)!);
        }

        using (harness)
        {
            var executor = new StepExecutor(harness, fixture.Given);
            var filed = new List<Guid>();
            var notImplemented = new List<string>();

            // Every filing the fixture performs, in the order it performed them: the card
            // invariants are checked on each, and a fixture's prior steps are filings like any
            // other. Checking only the step under test leaves a gate that broadcast no card at all
            // for a whole requirement level passing two thirds of the suite.
            var filings = new List<(string At, StepExecutor.StepResult Result)>();

            var priorIndex = 0;
            foreach (var prior in fixture.Given.Prior)
            {
                var result = await executor.RunAsync(prior, ct);
                Track(result, filed, notImplemented);
                if (result.IsFiling)
                {
                    filings.Add(($"prior[{priorIndex}].card", result));
                }

                priorIndex++;

                // A prior step's declared refusal is compared after that step runs.
                if (prior.RefusalStated)
                {
                    var mismatch = CompareRefusal("prior.refusal", prior.Refusal, result.Refusal);
                    if (mismatch is not null)
                    {
                        return new Outcome("fail", [mismatch], Reason(notImplemented));
                    }
                }
            }

            var under = await executor.RunAsync(fixture.Given.Step, ct);
            Track(under, filed, notImplemented);
            if (under.IsFiling)
            {
                filings.Add(("card", under));
            }

            var diff = new List<Mismatch>();

            // The step under test may declare its refusal on the step or state it in expect.error.
            // It may not state both differently; where it declares one, it is compared here too.
            if (fixture.Given.Step.RefusalStated)
            {
                var mismatch = CompareRefusal("step.refusal", fixture.Given.Step.Refusal, under.Refusal);
                if (mismatch is not null)
                {
                    diff.Add(mismatch);
                }
            }

            var observation = await ObserveAsync(fixture, harness, executor, under, filed, ct);
            Checker.Check(fixture.Expect, observation, harness.Telemetry.Emitted, diff);

            // GT-6 is a tripwire on every fixture, stated or not: the gate stands in front of writes
            // and must never perform one.
            if (harness.Executor.WasCalled)
            {
                diff.Add(Mismatch.Said("execute", "the gate never performs the write (GT-6)", "the write executor was called"));
            }

            // RUNNER.md §4.1: whenever a row carries an attestation, the runner also checks that it
            // names the entry it attests to. A record that cannot name its own subject is not
            // evidence (AZ-1).
            if (under.EntryId is { } id && await harness.Store.GetDocketEntryAsync(id, ct) is { } entry)
            {
                Observation.AttestationNamesItsSubject("entry", entry, diff);
            }

            // DRIVER.md §3: "The card invariants of RUNNER.md §4.2 are checked on EVERY filing,
            // whether or not the fixture mentions them." Every filing means every one the fixture
            // performed, not only the step under test — a prior step files through the same gate and
            // its card is subject to the same three facts. Not guarded on a card having been
            // broadcast either: a filing that broadcast none is precisely what this check is for —
            // the gate produces a card for every filing, including a Standing Order approval and a
            // blocked row (SR-4, AZ-4).
            foreach (var (at, filing) in filings)
            {
                if (filing.Filed is not { } filedEntry) continue;

                Observation.CardInvariants(filedEntry, filing.Card, diff, at);
            }

            return new Outcome(diff.Count == 0 ? "pass" : "fail", diff, Reason(notImplemented));
        }
    }

    private static Outcome WireUpRefused(Fixture fixture, Refusal refusal)
    {
        var diff = new List<Mismatch>();
        var answerable = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = refusal.Code, ["message"] = refusal.Message },
            ["store"] = new JsonObject { ["count"] = 0, ["pending"] = 0, ["approvedUnexecuted"] = 0 },
        };

        foreach (var (clause, _) in fixture.Expect)
        {
            if (clause is not ("error" or "telemetry" or "telemetryAbsent" or "store"))
            {
                diff.Add(Mismatch.Said(
                    clause,
                    "nothing: the gate refused at wire-up, so this clause is unanswerable (RUNNER.md §6)",
                    "the fixture states it"));
            }
        }

        Checker.Check(fixture.Expect, answerable, new HashSet<string>(StringComparer.Ordinal), diff);
        return new Outcome(diff.Count == 0 ? "pass" : "fail", diff, null);
    }

    private static void Track(StepExecutor.StepResult result, List<Guid> filed, List<string> notImplemented)
    {
        if (result.EntryId is { } id && !filed.Contains(id))
        {
            filed.Add(id);
        }

        if (result.NotImplemented is { } reason && !notImplemented.Contains(reason, StringComparer.Ordinal))
        {
            notImplemented.Add(reason);
        }
    }

    private static string? Reason(List<string> notImplemented) =>
        notImplemented.Count == 0 ? null : "not-implemented: " + string.Join(" | ", notImplemented);

    private static Mismatch? CompareRefusal(string at, string? expected, Refusal? actual)
    {
        var observed = actual?.Code;
        return string.Equals(expected, observed, StringComparison.Ordinal)
            ? null
            : Mismatch.Said(at, expected ?? "(no refusal)", observed ?? "(no refusal)");
    }

    private static async Task<JsonObject> ObserveAsync(
        Fixture fixture,
        GateHarness harness,
        StepExecutor executor,
        StepExecutor.StepResult under,
        IReadOnlyList<Guid> filed,
        CancellationToken ct)
    {
        var observation = new JsonObject
        {
            ["error"] = under.Refusal is null
                ? null
                : new JsonObject { ["code"] = under.Refusal.Code, ["message"] = under.Refusal.Message },
        };

        var rows = new List<DocketEntry>();
        foreach (var id in filed)
        {
            if (await harness.Store.GetDocketEntryAsync(id, ct) is { } row)
            {
                rows.Add(row);
            }
        }

        observation["store"] = new JsonObject
        {
            ["count"] = rows.Count,
            ["pending"] = rows.Count(r => r.Status == ReviewStatus.Pending),
            ["approvedUnexecuted"] = rows.Count(r => r.Status == ReviewStatus.Approved
                && r.Execution == ExecutionOutcome.Unexecuted),
        };

        if (under.EntryId is { } entryId && rows.FirstOrDefault(r => r.EntryId == entryId) is { } entry)
        {
            var parent = await harness.Store.GetResubmissionParentAsync(entryId, ct);
            observation["entry"] = Observation.Entry(
                entry,
                new Observation.EntryFacts(harness.Clock));

            if (parent is not null)
            {
                observation["superseded"] = Observation.Entry(
                    parent,
                    new Observation.EntryFacts(harness.Clock));
            }
        }

        if (under.Card is not null)
        {
            observation["card"] = Observation.Card(under.Card);
        }

        if (under.Expired is not null)
        {
            observation["expired"] = under.Expired;
        }

        if (under.Page is not null)
        {
            observation["page"] = under.Page;
        }

        if (under.Found is { } found)
        {
            observation["found"] = found;
        }

        // SR-1: expect.canonicalHash is the value the IMPLEMENTATION's own exported canonical-hash
        // helper produces, over the Affidavit combined with its accepted amendments — never
        // re-derived here, which is the substitution the rule exists to prevent.
        if (fixture.Expect.ContainsKey("canonicalHash")
            && under.EntryId is { } hashed
            && rows.FirstOrDefault(r => r.EntryId == hashed) is { } hashedRow)
        {
            observation["canonicalHash"] = Affiant.Core.Serialization.CanonicalSerializer.CanonicalHash(
                hashedRow.AmendedAffidavit ?? hashedRow.Envelope);
        }

        return observation;
    }
}
