# Handoff dossier — `Affiant.AgentFramework` (MAF adapter) proposal

> **Companion to:** [`affiant-maf-adapter.md`](affiant-maf-adapter.md) (the design) · **Anchored:** 2026-07-05 · **Purpose:** give a fresh reader — human or LLM, any provider, no conversation history, filesystem access to this public repo only — everything needed to continue this work. Update this file whenever the proposal changes.

## Originating motivation

Microsoft moved its .NET agent investment from **Semantic Kernel (SK)** to the **Microsoft Agent Framework (MAF)**, which reached 1.0 GA on **2026-04-03**. SK is in maintenance mode with a support floor of roughly **2027-04** ("≥ 1 year after MAF GA," per Microsoft's Agent Framework team devblog of 2025-10-07) and no published end date. Affiant's entire interception thesis rode on SK's function-invocation filter pipeline, so the maintainer decided (2026-06-24, reaffirmed 2026-07-05) to dual-target: keep SK first-class, add a MAF backend. On **2026-07-05** the maintainer commissioned the actual design and implementation — this proposal is the design that was previously missing (before it, the dual-target intent existed only as a decision plus a claims-verification memo, both in the private planning repo; their load-bearing content is inlined in the proposal).

Two verified facts bound the work (Microsoft primary sources, verified 2026-07-04):

1. **SK support floor, not EOL** — targeting MAF is a hedge, not an emergency.
2. **MAF middleware sees only client-invoked tools** — hosted/provider-side tools (hosted MCP, code interpreter, web/file search) bypass it, so Affiant on MAF can only swear to locally-invoked tool writes. The proposal makes this boundary structural (§4.6) and every doc surface states it.

## Who is acting on this

- **Seevali** — solo maintainer of the Affiant ecosystem; the only human in the loop; sole authority for merges, publishes, NuGet reservations, and anything irreversible ("operator acts"). Works with AI assistants under a standing rule that all substantive docs must be **cold-start portable** (readable by a fresh LLM with no memory).
- **AI conductor + sub-agents** (2026-07-05 session) — authored this proposal, implement it on feature branches, and deliver **draft PRs only**. An AI may push branches and open draft PRs; it may not merge, tag, publish, or flip repo visibility.

## Current project state (2026-07-05)

- This public repo (`Sakwala/affiant`) ships **eight** co-versioned packages at `1.0.0-alpha.1`. The first public release (`v1.0.0-beta.1`, **SK-only by design**) is engineering-ready but **not yet published** — the publish is a pending, operator-only, tag-triggered sequence. **Nothing in the MAF work may land on `main` or touch versioning/workflows until the maintainer merges it**; the MAF adapter is post-beta scope.
- Two private host applications (an aviation-maintenance copilot and an HR portal, in `Sakwala/affiant-host-apps`) validate the framework. The plan of record: the aviation host migrates to the MAF backend; the HR host deliberately stays on SK so both backends are exercised by a real host. (Host specifics live in the private repo; they are not needed to act on the proposal.)
- Implementation branch for this proposal: `feat/agent-framework-adapter` in this repo. Delivery vehicle: draft PR.
- Recon finding that shaped everything (2026-07-05): the "port is confined to one package" assumption was false — `Affiant.Core` and `Affiant.Abstractions` carried direct SK dependencies in violation of the spec's own L2 AC #4. The proposal's §3 documents the debt; §4.3 removes it.

## Decisions taken (with why)

| # | Decision | Why (compressed — full reasoning in proposal §) |
|---|---|---|
| 1 | One neutral pipeline in `Affiant.Core` + thin per-backend bridges, not per-backend filter copies | Only structure that makes cross-backend semantic drift impossible; duplication recreates the hollow-Affidavit regression class between backends (private-repo commit `b72c1fa`, 2026-04-30, not resolvable from this public repo) (§4.1) |
| 2 | Fix the Core/Abstractions SK-dependency violation as part of this work | The violation is exactly what blocks a second backend; deferring it makes the debt load-bearing (§3, §4.3) |
| 3 | Package name `Affiant.AgentFramework` | Symmetry with `Affiant.SemanticKernel` (product name minus vendor prefix); alternatives cryptic or Microsoft-sounding (§4.5). NuGet ID not yet reserved — operator act |
| 4 | Hosted tools: refuse by default, explicit per-tool acknowledgment to override | "Nothing writes without approval" must not silently exclude uncovered write paths; acknowledgment is itself an auditable record (§4.6). Maintainer may veto |
| 5 | MAF review gate prefers result replacement over `.Terminate` | Microsoft documents `.Terminate` side effects (skipped siblings, inconsistent history) (§2, §4.7) |
| 6 | Evidence sealing on MAF = middleware return value | .NET `FunctionInvocationContext` has no settable `.Result`; this is an API fact, not a preference (§2) |
| 7 | Don't port SK `Connectors/` machinery to MAF | `IChatClient` + `FunctionInvokingChatClient` already are the provider abstraction; re-wrapping duplicates MAF's job (§4.5) |
| 8 | net10.0 single-TFM, `Microsoft.Agents.AI` pinned 1.13.0 | Repo convention (no speculative multi-targeting); MAF supports net10.0; 1.x cadence carries breaking changes so pin + small surface (§4.5, §8) |
| 9 | SK hosts absorb mechanical wiring renames now | Pre-1.0 clean-break policy; behavior parity gated by existing SK suite + compliance harness (§4.4) |

## Open decisions

1. Hosted-tool default-refuse posture — maintainer confirm/veto (proposal §9.1).
2. NuGet reservation timing for `Affiant.AgentFramework` (§9.2).
3. Whether the ninth package ships in the next published version or stays repo-only until GA (§9.3).

## How to resume this work cold

1. Read the proposal, then spec §3.12/§4/§6 (paths in the proposal's reading order).
2. Check `git branch -a` for `feat/agent-framework-adapter` and any open draft PR on this repo — the implementation state of truth is the branch + PR, not this dossier.
3. The parity gates are executable: `dotnet build Affiant.slnx -c Release` (warning-clean), `dotnet test Affiant.slnx -c Release`, and the cross-backend ComplianceHarness suite (proposal §6). If those pass and the spec amendments of proposal §7 are present, the framework side is done.
4. Anything about host migrations or publish sequencing lives in the private repos and is the maintainer's concern.

## Glossary

See proposal §10 — kept in one place deliberately; this dossier adds no terms of its own.
