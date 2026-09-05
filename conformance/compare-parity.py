#!/usr/bin/env python3
"""Assert the failing set from a conformance run equals the parity manifest, exactly.

    conformance/compare-parity.py [results.json] [parity/dotnet-v0.1.json]

The set of fixture ids a run reports as `fail` or `error` must equal `failing[].id` in the
manifest. Any difference fails, in EITHER direction:

  * a fixture failing that the manifest does not list -- a regression, or a rule the
    implementation never met and nobody wrote down;
  * a fixture passing that the manifest still lists -- a gap closed and not published.

A check that caught only the first would let a fix rot unrecorded and the manifest would become a
document nobody trusts (affiant-protocol conformance/PARITY.md). `skipped` is not an escape hatch:
a skip is legitimate only where the manifest declares one, and this driver declares none.

The same comparison runs in-process as an xUnit assertion. It is here as well so the gate is
readable in a CI log without reading a test runner's output.
"""
import json
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent

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


RESULTS = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else run_log()
MANIFEST = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else HERE / "parity" / "dotnet-v0.1.json"


def die(message):
    print(f"compare-parity: {message}", file=sys.stderr)
    raise SystemExit(1)


for path in (RESULTS, MANIFEST):
    if not path.is_file():
        die(f"{path} does not exist. Run the conformance suite first (dotnet test).")

run = json.loads(RESULTS.read_text())
manifest = json.loads(MANIFEST.read_text())

if run["protocolTag"] != manifest["protocolTag"]:
    die(
        f"the run was made against protocol {run['protocolTag']} and the manifest names "
        f"{manifest['protocolTag']}. A manifest produced against one tag says nothing about another."
    )

if run["implementation"]["version"] != manifest["version"]:
    die(
        f"the run exercised {run['implementation']['version']} and the manifest is about "
        f"{manifest['version']}."
    )

observed = {r["id"] for r in run["results"] if r["outcome"] in ("fail", "error")}
skipped = {r["id"] for r in run["results"] if r["outcome"] == "skipped"}
declared = {row["id"] for row in manifest["failing"]}

regressed = sorted(observed - declared)
closed = sorted(declared - observed)

summary = run["summary"]
print(
    f"compare-parity: {run['implementation']['name']}@{run['implementation']['version']} "
    f"against protocol {run['protocolTag']}: {summary['passed']} passed, {summary['failed']} failed, "
    f"{summary['errored']} errored, {summary['skipped']} skipped of {summary['total']}."
)

problems = []
if regressed:
    problems.append("FAILING, NOT DECLARED (a regression, or a gap nobody wrote down):")
    problems += [f"    {i}" for i in regressed]
if closed:
    problems.append("DECLARED, NOW PASSING (a gap closed and not published):")
    problems += [f"    {i}" for i in closed]
if skipped:
    problems.append("SKIPPED, WHICH THIS MANIFEST DECLARES NONE OF:")
    problems += [f"    {i}" for i in sorted(skipped)]

if problems:
    print("\n".join(problems), file=sys.stderr)
    die(
        "the failing set and the parity manifest disagree. The manifest is a published claim about "
        "this implementation: regenerate it with conformance/regenerate-parity.py, read the diff, "
        "and put the change in the pull request."
    )

print(f"compare-parity: the failing set is exactly the {len(declared)} the manifest declares.")
