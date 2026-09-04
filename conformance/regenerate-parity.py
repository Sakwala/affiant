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

# disposition, detail, and the extra key that disposition requires.
CAUSES = {
    "GT-3-hollow-signature": (
        "planned",
        "The gate refuses a proposal that swears to nothing before anything is filed, and the "
        "refusal carries the protocol's substance-refused code. What it cannot do is name the "
        "hollow signature -- a field asserting a value while its provenance reads Empty -- because "
        "the schema-driven projection never carries a value it has no provenance for: by the time "
        "the Affidavit reaches the gate that field is valueless, and the refusal names the other "
        "signature, that no proposed field carries provenance other than Empty. Only an Affidavit "
        "built by a host's own projection can reach the gate hollow, and that one is refused with "
        "the sentence that names it.",
        {},
    ),
    "CV-4-coverage": (
        "fenced",
        "There is no coverage concept in the core. A tool the fixture declares uncovered is filed "
        "like any other and approved by the Standing Order that covers its risk: the row carries no "
        "blocked marker and the Evidence Card carries no warning naming the uncovered write, and no "
        "refusal is raised at wire-up either. The only coverage refusal in the tree is "
        "HostedToolAudit, an internal class inside the two adapter packages, raised when a tool "
        "catalogue is wired through WithAffiant.",
        {
            "fence": "Wire the tool catalogue through WithAffiant in Affiant.Extensions.AI or "
            "Affiant.AgentFramework: its HostedToolAudit refuses a tool list carrying "
            "provider-executed or hosted tools at start-up, before anything can be proposed. That "
            "closes the wire-up case only -- there is still no runtime refusal and no marker on a "
            "filed row."
        },
    ),
    "DK-2-resubmission": (
        "planned",
        "A reviewer's correction is preserved on the row it was typed on, with the instant and the "
        "person, and an accepted amendment is folded into the amended Affidavit. A resubmission "
        "does not read either: the new row is projected from the conversation again, so the "
        "corrected field comes back with the machine's value tagged Conversation and bound to "
        "nothing, where the record it supersedes holds that value as the reviewer's own act. The "
        "card broadcast for the resubmission carries no prior amendments, so the reviewer is asked "
        "the same question again with no sign that they already answered it.",
        {},
    ),
    "PV-1-tool-argument": (
        "planned",
        "A tool argument the model wrote and a value the member actually said are graded the same. "
        "ProvenanceTag.FromTool tags an argument Conversation at 0.9 -- the grade the rulebook "
        "reserves for what was read out of the turn -- so a literal from the utterance at the same "
        "confidence does not displace it and the incumbent stands. The card then shows the model's "
        "own argument where the member's words should be, tagged as though the member had said it. "
        "The merge itself is one comparison and is applied; what is wrong is the grade going in.",
        {},
    ),
    "SR-1-model": (
        "planned",
        "The canonical form the rulebook pins carries three things the Affidavit record does not: a "
        "protocol version, the conversation turn the proposal belongs to, and a created-at instant. "
        "The driver's independent canonicaliser reproduces the pinned bytes and the pinned digest "
        "for six of the seven vectors with no change to it, so what disagrees is the shape of the "
        "record and not the serialization -- and a fixture that pins a content hash cannot match a "
        "digest taken over a form this record cannot express.",
        {},
    ),
    "SR-1-amended-vector": (
        "planned",
        "The one vector whose sworn form is the Affidavit combined with its accepted amendments. "
        "The framework folds an accepted amendment into an amended Affidavit on approval, but a "
        "vector is a document rather than a filing: the driver builds it as JSON and has no "
        "accepted-amendment path to fold it through, so the pinned bytes and digest are compared "
        "against the unamended form. This vector also names the three properties the Affidavit "
        "record cannot hold.",
        {},
    ),
    "TL-1-registry": (
        "planned",
        "A registry event that only the framework's own hosted component emits. `docket.expired` is "
        "emitted by DocketExpiryService, the hosted scheduler; a host that schedules the sweep "
        "itself and calls IDocketStore.ExpireDueAsync directly -- which DK-3 explicitly sanctions "
        "-- records the expiry durably and emits nothing, so an operator counting expiries sees a "
        "number that depends on which of two supported wirings the host chose. The event belongs "
        "where the expiry is recorded rather than where it happens to be scheduled from.",
        {},
    ),
}

# Rows scheduled for a release other than PLANNED_FOR. `fixedIn` is for a release that has SHIPPED
# -- a version a reader can install -- and nothing here qualifies yet, so a row that named its own
# release would be `planned` and name it in `plannedFor`. There is no such row in this run.
SCHEDULED: dict[str, tuple[str, str]] = {}

# Where the ordered path scan is not the right reading, the fixture is named.
OVERRIDES = {
    "gate/substance-hollow-refused": "GT-3-hollow-signature",
    "gate/substance-zero-field-refused": "GT-3-hollow-signature",
    "gate/coverage-refused-declared": "CV-4-coverage",
    "sequence-a/coverage-refused-at-wire-up": "CV-4-coverage",
    "decide/resubmit-prefills": "DK-2-resubmission",
    "sequence-a/late-amendments-preserved": "DK-2-resubmission",
    "canonical/wire-evidence-card-request-amended": "SR-1-amended-vector",
}

# Most fundamental first: the first pattern a fixture's diffs match names its root cause.
SCAN = [
    ("CV-4-coverage", r"^(entry|superseded|card)\.blocked$"),
    ("DK-2-resubmission", r"^card\.priorAmendments$|^(entry|superseded)\.preservedAmendments"),
    ("SR-1-model", r"^canonicalHash$|^model\."),
    ("PV-1-tool-argument", r"\.fields\[\d+\]\.(value|source|confidence|bound|bindingKind|priorSources)"),
    ("GT-3-hollow-signature", r"^error"),
    ("TL-1-registry", r"^telemetry\["),
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
    "tree builds, read at the rulebook's v0.1.1. "
    "Five things a reader of this document alone should know. "
    "(1) WHAT THIS RUN IS. It is a BRANCH BUILD of the 1.0.0-beta.3 candidate, not a shipped "
    "release: the version in this manifest names what the tree builds, and nothing carrying it has "
    "been published. The release's own acceptance is a manifest whose failing list is EMPTY, so "
    "every row below is a gap still open in the candidate and every one is planned for that "
    "release. No row is \"fixed\", which names a version a reader can install; the coverage rows "
    "are \"fenced\", which names a host-side workaround that contains the gap today and does not "
    "end the work. "
    "(2) THE CLOCK IS INJECTABLE. Every instant the gate, the Docket and the sweep write comes from "
    "an injected TimeProvider; there is no DateTimeOffset.UtcNow anywhere in the packages. A "
    "fixture that moves its own clock moves the framework's, so the expiry, late-decision and "
    "resubmission fixtures are exercised rather than read off the API -- and where one of them "
    "still fails, the failure is about what the row carries, not about time. "
    "(3) THE CANONICAL VECTORS. The seven byte vectors are reproduced through the SHIPPED "
    "canonical serializer -- the same exported helper a host calls to mint an execution grant -- "
    "and never through a canonicaliser written beside the test. The rule says a driver reproduces "
    "the bytes and the digest rather than re-deriving them: the three paths that must agree are the "
    "implementation, a second canonicaliser written out from the rule (the rulebook's, which "
    "produced the pinned bytes), and an off-the-shelf SHA-256. The amended vector's sworn form is "
    "folded by the shipped amendment fold and checked against the accepted state the vector writes "
    "down, property for property, before its bytes are compared; every vector is held against "
    "canonical-vector.schema.json before it runs. "
    "(4) TELEMETRY. The framework emits the rulebook's registry names, and every telemetry clause "
    "in the suite is checked against them. One registry event is emitted from one wiring only: "
    "`docket.expired` comes from the hosted DocketExpiryService, so a host that schedules the sweep "
    "itself -- which DK-3 sanctions -- records the expiry durably and emits nothing. That is the "
    "single telemetry row below. "
    "(5) TWO SMALLER FINDINGS THE ROOT-CAUSE COLUMN DOES NOT NAME, both in the run log's diffs. An "
    "Evidence Card carries a presentation hint for every field whose kind is not text, where the "
    "suite expects a hint only for a field that actually constrains the reviewer's input -- a "
    "closed set or a pattern -- so a plain date field is sent a hint that says nothing. And the "
    "substance refusal names the signature it found rather than the one the fixture pins; see the "
    "GT-3 row for why the other signature cannot arise from the built-in projection."
)

if __name__ == "__main__":
    sys.exit(main())
