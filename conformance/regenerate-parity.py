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
adopt this framework needs -- what it does instead, and why that matters.
"""
import json
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
RESULTS = HERE / "results" / "dotnet-1.0.0-beta.1.json"
MANIFEST = HERE / "parity" / "dotnet-v0.1.json"
FIXTURE_INDEX = HERE.parent / "tests" / "Affiant.Conformance.Tests" / "protocol" / "fixtures" / "MANIFEST.json"
EXEMPTIONS = HERE.parent / "tests" / "Affiant.Conformance.Tests" / "protocol" / "lint" / "coverage-exemptions.json"

UNDECIDED = (
    "Undecided -- this row awaits the framework maintainer's ruling on when, or whether, it is "
    "closed. "
)

# disposition, detail, and the extra key that disposition requires.
CAUSES = {
    "AZ-1-attestation": (
        "ignored",
        UNDECIDED
        + "Nothing on a Docket row says who or what approved the write. DocketEntry has twelve "
        "properties and an attestation is not among them, so an approved row carries no "
        "attributable record of who is answerable for it; a Standing Order's approver id is "
        "computed by StandingOrderBase and only written to the log.",
        {},
    ),
    "DK-1-execution": (
        "ignored",
        UNDECIDED
        + "There is no execution state. Nothing records whether an approved write actually ran, so "
        "an approved-but-failed write is indistinguishable on the record from an "
        "approved-and-committed one, and there is no entry point for a host to report either.",
        {},
    ),
    "DK-1-decision": (
        "ignored",
        UNDECIDED
        + "A decision leaves a status and, on approval, a dictionary of amendments. There is no "
        "record of what was chosen and why, no timestamped preserved-amendments record, and an "
        "accepted amendment is never folded into an amended Affidavit -- so a later reader cannot "
        "see the state the approval accepted.",
        {},
    ),
    "AZ-4-blocked": (
        "ignored",
        UNDECIDED
        + "A row has no blocked marker, and a MultiParty verdict is routed to the same "
        "single-card branch as ReviewerConfirmation -- one approval satisfies a joint requirement. "
        "A referral is recorded as Deferred with nothing a card can show.",
        {},
    ),
    "GT-3-substance": (
        "ignored",
        UNDECIDED
        + "Substance is checked by the compliance harness at test time only. At run time the gate "
        "files whatever Affidavit it is handed, including one whose every field is tagged Empty -- "
        "a proposal that swears to nothing reaches a reviewer as though it swore to something.",
        {},
    ),
    "CV-4-coverage": (
        "fenced",
        "There is no coverage concept in the core. The only coverage refusal in the release is an "
        "internal wire-up audit inside the two adapter packages; nothing marks a filed row as "
        "blocked on coverage and there is no runtime refusal, so a tool the gate cannot intercept "
        "produces a proposal that looks like any other.",
        {
            "fence": "Wire the tool catalogue through WithAffiant in Affiant.Extensions.AI or "
            "Affiant.AgentFramework: its HostedToolAudit refuses a tool list carrying "
            "provider-executed or hosted tools at start-up, before anything can be proposed. That "
            "closes the wire-up case only -- there is still no runtime refusal and no marker on a "
            "filed row."
        },
    ),
    "AZ-2-decision": (
        "fenced",
        "The decision path consults no authorization port. ReviewGate.HandleDecisionAsync "
        "transitions any row whose id the caller knows -- from another tenant, or with no principal "
        "resolved at all -- and a host's authorization policy is never asked.",
        {
            "fence": "Authorize in the host before calling HandleDecisionAsync: resolve the "
            "principal, compare it and its tenant against the row's UserId and TenantId (read the "
            "row with IDocketStore.GetDocketEntryAsync first), and refuse there. The framework "
            "will not do it, and there is no seam that makes it."
        },
    ),
    "AZ-3-relay": (
        "ignored",
        UNDECIDED
        + "There is no identity model for a machine caller speaking for a person. A decision "
        "carries a single UserId string, so a relay asserting a member's identity and a member's "
        "own authenticated session are the same thing on the record.",
        {},
    ),
    "GT-4-ttl": (
        "ignored",
        UNDECIDED
        + "The deadline is stamped from one global default before the policy chain runs, and a "
        "policy's verdict is a bare enum that cannot name one. A re-file with the same id "
        "broadcasts a card carrying a freshly computed deadline while the row keeps its original, "
        "so a reviewer can be shown a deadline the record does not hold.",
        {},
    ),
    "GT-5-standing-order": (
        "ignored",
        UNDECIDED
        + "A policy cannot declare a risk ceiling or the provenance sources it predicates on: "
        "IApprovalPolicy returns a bare requirement and nothing else. A Standing Order that should "
        "be held back -- over its threshold, on an unbound input, or with a mandatory field left "
        "empty -- fires anyway, and approves with no person present.",
        {},
    ),
    "CV-1-wireup": (
        "ignored",
        UNDECIDED
        + "A wiring the gate should refuse is accepted. A policy cannot declare a threshold, so "
        "there is no declaration for the gate to notice is unbacked by a risk scorer, and the "
        "silent non-fire that follows is indistinguishable from a policy that had no opinion.",
        {},
    ),
    "DK-1-clock": (
        "ignored",
        UNDECIDED
        + "There is no clock seam anywhere in the release: ReviewGate reads DateTimeOffset.UtcNow "
        "at four sites and the expiry sweep at a fifth, none of them injectable. A row past its "
        "deadline reads pending until a background sweep happens to run, and a decision that "
        "arrives late is accepted rather than refused.",
        {},
    ),
    "AF-3-projection": (
        "fenced",
        "Every Affidavit the built-in projection produces is create-shaped: the entity id is "
        "hard-coded null and the previous value is null on every field, whatever the operation. A "
        "card for an update therefore cannot show what is changing, which is most of what a "
        "reviewer is being asked to check.",
        {
            "fence": "Register a host IAffidavitProjection in place of the schema-driven default. "
            "The interface is public and the registration replaces it; a host projection can set "
            "the entity id and fill each field's previous value from its own store before the "
            "proposal is filed."
        },
    ),
    "AF-2-confidence": (
        "ignored",
        UNDECIDED
        + "The Affidavit carries one confidence number and it is the mean over the non-Empty "
        "fields, so a mostly-empty Affidavit can report a high one. The record has no "
        "populatedConfidence and no empty-field count at all, so a reader cannot tell a confident "
        "record from a sparse one.",
        {},
    ),
    "PV-1-inference": (
        "ignored",
        UNDECIDED
        + "A value the host's inference reports is always tagged Inferred, whatever the port said "
        "about how it was found, and the merge is confidence-first with a source-ordinal "
        "tie-break. A value read literally out of the turn therefore loses to the model's own "
        "argument at equal confidence, and the argument is what gets filed.",
        {},
    ),
    "PV-2-binding": (
        "ignored",
        UNDECIDED
        + "A provenance tag carries a source, a confidence, an evidence string and a conversation "
        "turn, and nothing that points at a record an auditor could re-fetch. No value is ever "
        "bound, so nothing can tell a value checked against a system of record from one a model "
        "asserted.",
        {},
    ),
    "DK-3-sweep": (
        "ignored",
        UNDECIDED
        + "The expiry sweep loads every pending entry on every instance and reports nothing: it "
        "takes no limit, no cursor and no scope, and returns void. A docket large enough to matter "
        "is swept in one unbounded read.",
        {},
    ),
    "DK-5-rehydrate": (
        "ignored",
        UNDECIDED
        + "The rehydration surface re-broadcasts a session's pending cards and returns void. There "
        "is no page, no cursor, no more flag and no order a reconnecting client can read, so a "
        "client cannot tell a complete rehydration from a partial one.",
        {},
    ),
    "SR-1-canonical": (
        "ignored",
        UNDECIDED
        + "There is no canonical serialization and no content hash in the release. Nothing can "
        "bind an execution grant to the exact Affidavit a reviewer accepted, which is the "
        "substitution the rule exists to prevent.",
        {},
    ),
    "SR-1-model": (
        "ignored",
        UNDECIDED
        + "The canonical form is reproduced byte for byte by a canonicaliser written out from the "
        "rule, but the .NET model cannot hold the shape the vector pins: the Affidavit record has "
        "no populated-confidence and no empty-field count, and a provenance tag has no binding. "
        "The release also exports no canonical-hash helper, so there is nothing to compare against.",
        {},
    ),
    "SR-4-card": (
        "ignored",
        UNDECIDED
        + "The Evidence Card carries a docket id, an Affidavit, a deadline and prior amendments -- "
        "and no protocol version. A consumer cannot tell which version of the envelope it "
        "received, which is checked on every filing and so fails on every filing.",
        {},
    ),
}

# The one row with a named release behind it.
FIXED = {
    "gate/standing-order-by-the-book": (
        "1.0.0-beta.1.1",
        "The shipped default risk scorer never returns the grade the default Standing Order "
        "threshold demands, so a by-the-book Standing Order can never fire -- corrected by the "
        "risk-floor change in 1.0.0-beta.1.1 (Sakwala/affiant#53). This row remains open at "
        "1.0.0-beta.1 for a second reason that release does not close: the row the Standing Order "
        "approves still carries no attestation and no execution state.",
    ),
}

# Where the ordered path scan is not the right reading, the fixture is named.
OVERRIDES = {
    "gate/substance-hollow-refused": "GT-3-substance",
    "gate/substance-zero-field-refused": "GT-3-substance",
    "gate/threshold-without-scorer": "CV-1-wireup",
    "gate/coverage-refused-declared": "CV-4-coverage",
    "sequence-a/coverage-refused-at-wire-up": "CV-4-coverage",
    "decide/relay-without-assertion-refused": "AZ-3-relay",
    "sequence-c/relay-may-not-attest-member": "AZ-3-relay",
    "decide/resubmit-prefills": "DK-1-clock",
    "sequence-a/late-amendments-preserved": "DK-1-clock",
    "decide/expired-amendments-preserved": "DK-1-clock",
    "sequence-a/expiry-then-resubmit": "DK-1-clock",
    "decide/execution-on-pending-refused": "DK-1-execution",
    "sequence-a/interleaved-conversations": "AF-3-projection",
    "canonical/create-shaped": "SR-1-model",
    "canonical/update-shaped": "SR-1-model",
    "canonical/money-and-escapes": "SR-1-model",
    "canonical/wire-evidence-card-request-amended": "SR-1-canonical",
}

# Most fundamental first: the first pattern a fixture's diffs match names its root cause.
SCAN = [
    ("AZ-1-attestation", r"^(entry|superseded)\.attestation$"),
    ("DK-1-execution", r"^(entry|superseded)\.execution"),
    ("AZ-4-blocked", r"^(entry|superseded)\.blocked$|^card\.blocked$"),
    ("AZ-2-decision", r"^error$|^prior\.refusal$|^step\.refusal$"),
    ("GT-5-standing-order", r"^(entry|superseded)\.status$"),
    ("AF-3-projection", r"affidavit\.entityId$|previousValue$"),
    ("GT-4-ttl", r"expiresAtOffsetMs$|^card\.requiredBy$"),
    ("DK-1-decision", r"^entry\.(decision|amendments|preservedAmendments|amendedAffidavit)$"),
    ("AZ-4-blocked", r"^entry\.requirement$"),
    ("DK-3-sweep", r"^expired\."),
    ("DK-5-rehydrate", r"^page\."),
    ("SR-1-canonical", r"^canonicalHash$"),
    ("PV-2-binding", r"\.(bound|bindingKind)$"),
    ("AF-2-confidence", r"(populatedConfidence|emptyFieldCount|aggregateConfidence)$"),
    ("PV-1-inference", r"affidavit\.fields\[\d+\]\.(value|source|confidence|priorSources)"),
    ("SR-4-card", r"^card\.protocolVersion$"),
    ("DK-5-rehydrate", r"^card$"),
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
    run = json.loads(RESULTS.read_text())
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

        if result["id"] in FIXED:
            fixed_in, detail = FIXED[result["id"]]
            row["disposition"] = "fixed"
            row["detail"] = detail
            row["fixedIn"] = fixed_in
        else:
            disposition, detail, extra = CAUSES[cause_of(result)]
            row["disposition"] = disposition
            row["detail"] = detail
            row.update(extra)

        if fixture.get("oracle"):
            row["oracle"] = True

        failing.append(row)

    manifest = {
        "schemaVersion": "0.1.0",
        "implementation": run["implementation"]["name"],
        "version": run["implementation"]["version"],
        "protocolTag": run["protocolTag"],
        "producedAt": run["producedAt"],
        "runLog": "conformance/results/dotnet-1.0.0-beta.1.json (Sakwala/affiant)",
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
    "First run of the .NET conformance driver, against the shipped packages at 1.0.0-beta.1. "
    "Two things a reader of this document alone should know. "
    "(1) Most rows carry disposition \"ignored\" with a detail beginning \"Undecided\": the format "
    "has three dispositions and no fourth, and these gaps have been measured and written down but "
    "not yet ruled on by the framework's maintainer. They are listed for decision in the pull "
    "request that produced this manifest; \"ignored\" here means \"nothing is being done YET\", not "
    "\"nothing will be\". "
    "(2) The framework emits telemetry under names that share nothing with the rulebook's registry "
    "-- it emits affidavit.projected, inference.completed and affiant.review.broadcast_failed where "
    "the registry names affidavit.filed, docket.transition and standing-order.fired -- so every "
    "telemetry clause in the suite fails and every telemetryAbsent clause holds. That is a true "
    "statement about this release; no name was translated to make a clause pass."
)

if __name__ == "__main__":
    sys.exit(main())
