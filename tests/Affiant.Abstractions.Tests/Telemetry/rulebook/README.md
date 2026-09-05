# Vendored from the Affiant protocol rulebook

Byte-identical copies, taken so this repository's telemetry-key registry can be validated against
the rulebook's own schema without a network fetch or a submodule at test time. Diff them against
upstream to confirm they have not drifted; do not edit them here.

| File | Upstream path |
|---|---|
| `telemetry-key.schema.json` | `schemas/0.1.0/telemetry-key.schema.json` |
| `common.schema.json` | `schemas/0.1.0/common.schema.json` (holds the `protocolVersion` `$def` the telemetry schema `$ref`s) |
| `fixture-01-registry.json` | `conformance/fixtures/v0.1/telemetry-key/01-registry.json` — the rulebook's **positive** fixture |
| `fixture-90-key-without-attributes.json` | `conformance/fixtures/v0.1/telemetry-key/90-key-without-attributes.json` — the rulebook's **negative** fixture |

- **Repository:** `Sakwala/affiant-protocol`
- **Commit:** `242964faba9e6852b8fbfcdef6c3296b5c705f59` (2026-09-04), protocol v0.1 schemas
- **Rules these serve:** `INVARIANTS.md` TL-1 (the registry is a versioned API) and TL-2 (standards
  vocabulary — the `gen_ai.*` attribute names).

The two fixtures are here for a specific reason: `TelemetryKeyRegistryTests` validates against the
schema with a small validator written in the test project, and a validator nobody tests is a
validator that passes everything. Running the rulebook's own positive fixture (must pass) and
negative fixture (must fail, because a registry entry with no `attributes` array is not a registry
entry) is what proves it is doing work.

When the rulebook cuts a new schema revision, re-copy all four files, update the commit above, and
re-run the suite. A schema change that this repository's registry no longer satisfies is exactly
what these tests exist to catch.
