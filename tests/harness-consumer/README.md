# harness-consumer

A console project whose only Affiant reference is the **packed**
`Affiant.Testing.ComplianceHarness`, restored from a local feed — an adopter, not a sibling project.
It runs the rulebook's conformance suite from a directory it names itself, deliberately not the one
beside the assembly, and fails unless every fixture passes. CI's `harness-consumer` job packs the
release, restores this against that feed alone, and runs it; the defect it exists to catch is a
package that quietly reads its own copy of the rulebook instead of the caller's.

Reproducing it locally: isolate the package cache first (`NUGET_PACKAGES` pointed at a job-local
folder, or the package-source mapping plus `--packages` the CI job uses). A `1.0.0-beta.3` harness
already in the global cache from an earlier pack wins the restore silently, and the run then measures
the wrong assembly.
