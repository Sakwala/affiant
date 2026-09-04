# Conformance

This directory is how the Affiant .NET packages are measured against the written protocol, and how
the measurement is published.

The protocol lives in [`Sakwala/affiant-protocol`](https://github.com/Sakwala/affiant-protocol): the
numbered invariants in prose (`INVARIANTS.md`), the JSON Schemas for the wire, and a suite of
**declarative conformance fixtures** — 56 documents that each state a wiring, a sequence of acts and
what must then be true, plus 7 canonical byte vectors. The fixtures name no class, no file and no
language. The thing that binds them to this implementation is the **driver**,
`tests/Affiant.Conformance.Tests`, and the thing the driver produces is a **parity report** naming
exactly which fixtures this implementation does not pass and why.

## The files

| Path | What it is |
|---|---|
| `PROTOCOL_PIN` | The protocol ref this repository is pinned to. Bumping it is a reviewable diff in this repository's own history, so a format change never arrives as a silent upstream shift under a running build. |
| `sync.sh` | Vendors the pinned suite into `tests/Affiant.Conformance.Tests/protocol/` with a `SHA256SUMS`. `sync.sh --verify` re-checks the copy and is what CI runs, so a local edit to a vendored fixture cannot pass unnoticed. |
| `parity/dotnet-v0.1.json` | **The parity report.** Every fixture this implementation does not pass, with the rule, what it does instead, and its disposition. The published copy lives in the rulebook repository beside the fixtures; this is the copy CI asserts against. |
| `results/dotnet-1.0.0-beta.1.json` | The run the report's claim rests on — one entry per fixture, including the ones that passed. Rewritten every time the suite runs. |
| `results/ORACLE-RUN-1.0.0-beta.1.md` | That run read against the rulebook's negative oracle: for each fixture the oracle says must fail on this release, whether it did and whether it failed for the recorded reason. |
| `compare-parity.py` | The CI gate: the set of ids a run reports as `fail` or `error` must equal `failing[].id` in the report, exactly. |
| `regenerate-parity.py` | Rewrites the report from the latest run. Never run automatically: a change to the failing set is a change to a published claim and belongs in a pull request a person read. |

## The rule CI enforces

**The failing set equals the parity report, exactly** — and a difference in *either* direction fails
the build:

- a fixture failing that the report does not list: a regression, or a rule this implementation never
  met and nobody wrote down;
- a fixture passing that the report still lists: a gap that has been closed and not published.

A check that caught only the first would let a fix rot unrecorded and the report would drift into a
document nobody trusts.

## Changing something

- **A fix in `src/` closes a gap.** Run `dotnet test tests/Affiant.Conformance.Tests`, then
  `conformance/regenerate-parity.py`, read the diff, and put it in the same pull request as the fix.
- **The rulebook moves.** Edit `PROTOCOL_PIN`, run `conformance/sync.sh`, run the suite, regenerate
  the report. The vendored diff and the report diff land together.
- **A fixture looks wrong.** Do not edit the vendored copy — `sync.sh --verify` will catch it, and an
  edited fixture is no longer the document the comparison is about. Raise it against the rulebook.

## What the driver does not share with the compliance harness

`Affiant.Testing.ComplianceHarness` and this driver were expected to share a small internal library of
fixture-loading primitives. They do not, and the reason is worth recording rather than revisiting:
**the harness has no fixture-loading primitives to share.** It has no file I/O, no serialization and
no scenario format — its fixtures are C# classes discovered through dependency injection, and each
case's assertion is a compiled `Func<Affidavit, bool>`, which is precisely why it has no on-disk
format. Its one genuinely reusable piece is `AssertProvenanceIsSubstantive`, and the driver must not
use it: that predicate is the *test-time* substance check, and the rule the conformance suite is
about (GT-3) is whether the **runtime** refuses a hollow Affidavit. Calling the harness's checker
would turn a fixture that pins a real gap into one that passes. There is no clean extraction here,
only a resemblance.
