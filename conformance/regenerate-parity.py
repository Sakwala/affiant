#!/usr/bin/env python3
"""Regenerate conformance/parity/dotnet-v0.1.json from the latest run.

    conformance/regenerate-parity.py

The manifest is this implementation's published statement of exactly which conformance fixtures it
does not pass, and why (affiant-protocol conformance/PARITY.md). It is regenerable -- this script --
but NEVER auto-committed: a change to the failing set is a change to a published claim about an
implementation and belongs in a pull request a person read. The script writes the file; a person
reads the diff and decides whether the claim it now makes is one this project stands behind.

Every failing fixture is attributed to ONE root cause, chosen by the most fundamental unmet rule
among its diffs. The cause carries the disposition and the sentence a reader deciding whether to
adopt this framework needs -- what it does instead, and why that matters. The dispositions are the
rulebook's four: `fixed` (a SHIPPED release corrects it, named in `fixedIn`), `planned` (scheduled
for a named release, in `plannedFor`), `fenced` (a host-side workaround contains it now, and the fix
is still scheduled) and `ignored` (nothing is being done). Nothing here is `ignored`, and nothing is
`fixed` either: every correction is on a branch or on a schedule, and no release carrying one has
shipped.

Every sentence below is written from a diff in the run log named in `runLog` and describes the tree
that produced it. A fixture whose diffs match no cause here stops this script rather than being
given the nearest old sentence: a detail that has outlived the defect it describes is worse than no
detail, because it reads as a measurement.
"""
import json
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
MANIFEST = HERE / "parity" / "dotnet-v0.1.json"
FIXTURE_INDEX = HERE.parent / "tests" / "Affiant.Conformance.Tests" / "protocol" / "fixtures" / "MANIFEST.json"
EXEMPTIONS = HERE.parent / "tests" / "Affiant.Conformance.Tests" / "protocol" / "lint" / "coverage-exemptions.json"


def built_version():
    """The version this tree builds, from Directory.Build.props.

    The run log is named after the version it measured, so a script looking for it has to ask the
    same question the driver did rather than carry a constant that goes stale the moment the tree is
    versioned for the next release.
    """
    props = (HERE.parent / "Directory.Build.props").read_text()
    prefix = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", props)
    suffix = re.search(r"<VersionSuffix>([^<]*)</VersionSuffix>", props)
    if not prefix:
        raise SystemExit("no <VersionPrefix> in Directory.Build.props")
    return f"{prefix.group(1)}-{suffix.group(1)}" if suffix and suffix.group(1) else prefix.group(1)


def run_log():
    """The run this tree's own build wrote."""
    return HERE / "results" / f"dotnet-{built_version()}.json"


# The release every open gap below is scheduled for. A row that names it carries disposition
# "planned": the gap is measured, written down and on the schedule for a named release. That is a
# different statement from "ignored" (nothing is being done) and the rulebook's parity format has a
# value for each -- see affiant-protocol conformance/PARITY.md.
PLANNED_FOR = "1.0.0-beta.3"

# disposition, detail, and the extra key that disposition requires. A fixture whose diffs match no
# cause here stops this script with its own id rather than being given the nearest sentence that once
# fit something else -- a detail that has outlived the defect it describes is worse than no detail,
# because it reads as a measurement.
CAUSES = {
    "SR-1-pin-predates-correction": (
        "planned",
        "The two fixtures that pin a CONTENT HASH are pinned against a value the rulebook is "
        "correcting. SR-1's canonical form is taken over the Affidavit as the rulebook's own schema "
        "defines it -- protocol version included, which is a required property of that record and "
        "is carried in the expected bytes of all seven canonical byte vectors. The hashes these two "
        "fixtures pin were produced by the reference implementation's RUNTIME, over a model that "
        "does not carry the property, so the two artifacts disagree with each other and no "
        "implementation can satisfy both. This implementation follows the schema and the vectors: "
        "its canonical form carries the version, all seven vectors reproduce byte for byte and "
        "digest for digest, and the typed and document paths agree with each other over a record "
        "that states one. Everything else in both fixtures passes, the entry id inside the amended "
        "record's reviewer-act binding included. The rulebook is re-pinning both hashes at v0.1.2; "
        "these rows close when the suite vendored here moves to that tag.",
        {},
    ),
}

# Rows scheduled for a release other than PLANNED_FOR. `fixedIn` is for a release that has SHIPPED
# -- a version a reader can install -- and nothing here qualifies yet, so a row that named its own
# release would be `planned` and name it in `plannedFor`. There is no such row in this run.
SCHEDULED: dict[str, tuple[str, str]] = {}

# Where the ordered path scan is not the right reading, the fixture is named.
OVERRIDES: dict[str, str] = {}

# Most fundamental first: the first pattern a fixture's diffs match names its root cause.
SCAN = [
    ("SR-1-pin-predates-correction", r"^canonicalHash$"),
]


def cause_of(result):
    if result["id"] in OVERRIDES:
        return OVERRIDES[result["id"]]
    paths = [d["at"] for d in result.get("diff", [])]
    for name, pattern in SCAN:
        if any(re.search(pattern, p) for p in paths):
            return name
    raise SystemExit(f"regenerate-parity: no root cause matched {result['id']} ({paths})")


def main():
    results = run_log()
    run = json.loads(results.read_text())
    index = {f["id"]: f for f in json.loads(FIXTURE_INDEX.read_text())["conformance"]["fixtures"]}
    exemptions = json.loads(EXEMPTIONS.read_text())["exemptions"]

    failing = []
    for result in run["results"]:
        if result["outcome"] not in ("fail", "error"):
            continue

        fixture = index[result["id"]]
        row = {
            "id": result["id"],
            "rules": fixture["rules"],
        }

        if result["id"] in SCHEDULED:
            planned_for, detail = SCHEDULED[result["id"]]
            row["disposition"] = "planned"
            row["detail"] = detail
            row["plannedFor"] = planned_for
        else:
            disposition, detail, extra = CAUSES[cause_of(result)]
            row["disposition"] = disposition
            row["detail"] = detail
            row.update(extra)
            # A fence is the honest disposition today; the fix behind it is still scheduled, so a
            # fenced row names the release too. A planned row must.
            if disposition in ("planned", "fenced"):
                row["plannedFor"] = PLANNED_FOR

        if fixture.get("oracle"):
            row["oracle"] = True

        failing.append(row)

    manifest = {
        "schemaVersion": "0.1.0",
        "implementation": run["implementation"]["name"],
        "version": run["implementation"]["version"],
        "protocolTag": run["protocolTag"],
        "producedAt": run["producedAt"],
        "runLog": f"conformance/results/{results.name} (Sakwala/affiant)",
        "failing": failing,
        "runtimes": [{"name": "net10.0", "version": "10.0", "claimed": True}],
        "exemptions": [
            {
                "rule": e["rule"],
                "until": e["until"],
                "reason": e["reason"],
                "checkedInstead": CHECKED_INSTEAD[e["rule"]],
            }
            for e in exemptions
        ],
        "notes": NOTES,
    }

    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"regenerate-parity: wrote {len(failing)} failing rows to {MANIFEST}")
    print("regenerate-parity: read the diff before committing. This is a published claim.")


CHECKED_INSTEAD = {
    "SR-5": "the wire-shape suites in Sakwala/affiant-host-apps, which compare each host payload's key set against the shipped serializer",
    "CV-2": "no substitute yet: the adapter call-site fixtures arrive with the rulebook's v0.2",
    "CV-3": "no substitute yet: the delegation fixtures arrive with the rulebook's v0.2",
    "CV-5": "no substitute yet: the adapter documentation lint arrives with the rulebook's v0.2",
    "AF-5": "the Affiant.Abstractions.Tests suite over the tool-result envelope types",
    "SR-3": "the fixture lint in the rulebook, run over every vendored fixture by conformance/sync.sh --verify",
    "RT-1": "not claimed beyond one runtime: this implementation targets net10.0 only, and the manifest names that one runtime",
    "RT-2": "no per-request resource budget suite exists in this repository yet",
    "RT-3": "the public-API baselines (Microsoft.CodeAnalysis.PublicApiAnalyzers) plus TreatWarningsAsErrors, which fail the build on an undeclared public member",
    "TL-1": "the Affiant.Core.Tests observability suite over AffiantTelemetry; see the note on the disjoint key sets",
    "TL-2": "no substitute: the framework's telemetry attribute names are not checked against the published standards",
}

NOTES = (
    "The .NET conformance driver, run by this repository's own test suite against the packages this "
    "tree builds, read at the rulebook's v0.1.1. Sixty-one of the sixty-three fixtures pass, and "
    "the two that do not are pinned against a value the rulebook is correcting -- see the row's "
    "detail, and (2) below. "
    "Four things a reader of this document alone should know. "
    "(1) WHAT THIS RUN IS. It is a BRANCH BUILD of the 1.0.0-beta.3 candidate, not a shipped "
    "release: the version here names what the tree builds, and nothing carrying it has been "
    "published. "
    "(2) THE TWO OPEN ROWS ARE A PIN, NOT A GAP. SR-1's canonical form is over the Affidavit as the "
    "rulebook's schema defines it, protocol version included, and every canonical byte vector's "
    "expected bytes carry it. The content hashes those two fixtures pin were produced by the "
    "reference implementation's runtime over a model that does not carry the property, so the "
    "rulebook's two artifacts disagree and no implementation can satisfy both. This one follows the "
    "schema and the vectors. Both fixtures are being re-pinned at the rulebook's v0.1.2, and these "
    "rows close when the vendored suite moves to that tag. "
    "(3) THE CANONICAL FORM AND THE ENTRY ID ARE THE PROTOCOL'S. Every byte vector is reproduced "
    "through the SHIPPED serializer -- the same exported helper a host calls to mint an execution "
    "grant -- and never through a canonicaliser written beside the test: the rule says a driver "
    "reproduces the bytes and the digest rather than re-deriving them. The amended vector's sworn "
    "form is folded by the shipped amendment fold and checked against the accepted state the vector "
    "writes down before its bytes are compared, and every vector is validated against "
    "canonical-vector.schema.json before it runs. A Docket entry id is derived from the tenant, the "
    "conversation, the tool and the canonical form of the operation and its arguments, digested and "
    "laid out as the rule states, so an id minted here and an id minted by another implementation "
    "for the same proposal are the same id -- which they must be, because the id travels inside the "
    "record and therefore inside the hash a grant binds to. "
    "(4) WHAT THE DRIVER CHECKS WITHOUT BEING ASKED. Every filing a fixture performs -- prior steps "
    "included -- is card-checked: the card points at its row, carries that row's deadline and "
    "protocol version, repeats the record's three confidence numbers, and says on its face when the "
    "row is blocked. Every attestation is checked to name the entry it attests to. `wrap-execute` "
    "runs the shipped tool-wrapping pipeline rather than a restatement of it, and the driver's "
    "README names the entry point each of the eight step kinds is bound to."
)

if __name__ == "__main__":
    sys.exit(main())
