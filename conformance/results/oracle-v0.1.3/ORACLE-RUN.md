# The negative oracle at `v0.1.3`, run against the published `1.0.0-beta.3`

**What this file is.** The rulebook's `conformance/ORACLE.md` carries a second table at `v0.1.3`: the
five fixtures that must **fail** against `Sakwala/affiant` `1.0.0-beta.3`, the release deployed to the
demo hosts on 2026-09-06, because PV-3 at `v0.1.3` says the implementation establishes presence from
the utterance and that release takes the inference port's word for it. A fixture whose rule a known
defective release violates is accepted into the suite only if it fails against that release, and a
table is read off a release's source until it is **run**. This is that run, from this repository,
which is where the rulebook says the published run document belongs: a result document names the
protocol tag from the `PROTOCOL_PIN` an implementation vendors, and the rulebook vendors none.

- **Measured:** `Affiant.Testing.ComplianceHarness` `1.0.0-beta.3` and its four sibling packages
  (`Affiant.Core`, `Affiant.Abstractions`, `Affiant.Docket`, `Affiant.Policies`), restored **from
  nuget.org** — the published packages, not this tree — by a scratch console consumer whose only
  Affiant reference is the package, with an isolated `NUGET_PACKAGES` so a locally packed `beta.3` in
  the global cache could not win the restore and be measured instead. The run document names the
  implementation commit the published assembly carries, `436c5e23822b44a2857fb2a0232df900545a72f8`,
  which is the `v1.0.0-beta.3` tag.
- **Against:** the fixture tree this repository vendors at the rulebook's **`v0.1.3`** tag
  (`conformance/PROTOCOL_PIN`, commit `cfb476431aa4d6bbaef9cacbce5bab75053e67c8`) —
  `tests/Affiant.Conformance.Tests/protocol`, 68 documents.
- **Run:** [`dotnet-1.0.0-beta.3.json`](dotnet-1.0.0-beta.3.json), produced 2026-09-08T16:53:28.211Z.
  It is in **this** directory and not in `conformance/results/`, because the harness names a run after
  the version it measured and `conformance/results/dotnet-1.0.0-beta.3.json` is already the published
  record of that release measured at `v0.1.2`. Two different questions, two files.
- **Result:** **62 passed, 3 failed, 3 errored, 0 skipped of 68.** Every one of the five fixtures the
  oracle lists did not pass. Nothing outside the table failed except `gate/inference-empty-value-is-nothing-reported`,
  which the oracle lists against no release — see *One fixture the oracle does not list* below.

The other half of the assertion is this tree: the same 68 documents against the branch that fixes
[Sakwala/affiant#123](https://github.com/Sakwala/affiant/issues/123) — **68 passed, 0 failed** —
recorded in [`../dotnet-1.0.0-beta.3.1.json`](../dotnet-1.0.0-beta.3.1.json) and claimed in
`conformance/parity/dotnet-v0.1.json`. A fixture that failed on the release and passes on the fix is
the whole of what a negative oracle is for.

## The five the oracle lists

| Fixture | Rules | Outcome | What the run observed | The defect the oracle records |
|---|---|---|---|---|
| `gate/inference-presence-computed-from-the-utterance` | GT-1, PV-1, PV-3, AF-1, AF-2 | **error** | `NullReferenceException` out of the release's own fixture loader, before the gate ran | Presence is taken from the port's `presence` property alone, and no shipped port reports it — and on the shipped packages this fixture does not reach the gate at all, because the `beta.3` loader reads `presence` as required where `v0.1.3` makes it optional |
| `gate/inference-case-folds-and-the-digest-is-the-utterances` | GT-1, PV-1, PV-3, AF-1, AF-2 | **error** | the same `NullReferenceException`, for the same reason | as above |
| `gate/inference-port-literal-unconfirmed` | GT-1, PV-1, PV-3, AF-1, AF-2 | **fail** | `source` `Conversation` where the fixture expects `Inferred`; `bound` true where it expects false; `bindingKind` `utterance-span` where it expects null | A port's `presence: "literal"` is honoured unverified: a value that is not in the utterance is graded `Conversation` and bound to the span the port named |
| `gate/inference-port-span-fails-the-boundary` | GT-1, PV-1, PV-3, AF-1, AF-2 | **fail** | the same three: `Conversation`, bound, `utterance-span` | A span naming text inside a longer token (`20` inside `2026-09-08`) is minted as given |
| `sequence-a/picker-external-binding` | PV-1, PV-2, PV-3, GT-1 | **fail** | `bound` false where the fixture expects true; `bindingKind` null; the pinned `utteranceSpan` absent | A binding is minted only where the port named a span: a value the person typed carries no binding when the port reported none |

Each failure is the one its row describes. Nothing was tuned to make anything fail, and no fixture
was edited: `conformance/sync.sh --verify` re-checks the vendored tree against `SHA256SUMS` and passes
at this pin.

One line of the `sequence-a/picker-external-binding` diff is a gap in **that driver** rather than a
defect in the release, and the rulebook says so: the `beta.3` harness does not implement the field
matcher's `utteranceSpan` key at all, so a pinned span reads as absent under it whatever the release
mints. The evidence against the release there is `bound: false` and `bindingKind: null`, which the
same run reports.

## The two that error rather than fail

`gate/18` and `gate/21` state a port that reports `value` and `confidence` and nothing else — which is
what every shipped inference port reports. The `1.0.0-beta.3` fixture loader reads `presence` as a
required key, so it never builds the port and the fixture is an `error` outcome rather than a `fail`.
An error is not a pass and counts against the implementation exactly as a failure does
(`RUNNER.md` §8), so the oracle's rule — a listed fixture must not pass — holds. The grading defect
underneath is the one the rulebook's own probe shows: scripting `presence: "inferred"` into those two
documents, which is `beta.3`'s own spelling of a port that claimed nothing, grades the field
`Inferred` and unbound where the fixture expects `Conversation` and bound.

## One fixture the oracle does not list

`gate/inference-empty-value-is-nothing-reported` was authored at `v0.1.3` and is listed against no
release. **It errored here**, with the same `NullReferenceException` from the `beta.3` loader as
`gate/18` and `gate/21`, and for the same reason: it too states a port that reports `value` and
`confidence` only. `ORACLE.md` says of it that "this one is expected to pass" on `beta.3`, reasoning
from the release's grading path — which is right about the grading and wrong about the loader, which
the fixture never gets past. A fixture not listed against a release MAY pass or fail on it, so nothing
about the oracle's rule changes; the sentence about what this one would do is a claim this run
corrects, and it is written down here rather than left standing. The rulebook is tagged at `v0.1.3`,
so the correction belongs to a later rulebook pass, not to this file's own tree.

## Reproducing it

```sh
# A scratch consumer of the PUBLISHED packages, in its own package cache.
export NUGET_PACKAGES=<a job-local folder>
dotnet restore <consumer.csproj> --packages "$NUGET_PACKAGES"   # PackageReference Affiant.Testing.ComplianceHarness 1.0.0-beta.3
dotnet run --project <consumer.csproj> -c Release -- \
  tests/Affiant.Conformance.Tests/protocol <a results directory>
```

The consumer calls `ConformanceSuite.Run(protocolRoot, writeRunTo)` and nothing else. Isolating the
package cache is not optional: a `1.0.0-beta.3` harness left in the global cache by an earlier local
pack wins the restore silently, and the run then measures this repository's own tree wearing the
published version's name.
