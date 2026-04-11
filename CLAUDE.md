# CLAUDE.md — Affiant Framework

## What this is

`packages/` is the Affiant framework — the future open-source NuGet release. It provides "sworn provenance for every AI write": a Semantic Kernel-based layer with a two-filter context fabric, field-level provenance tracking, and a durable review queue (the Docket) with Evidence Cards and Referrals. Everything in this subtree must be reusable across any host application. Nothing in this subtree is allowed to reference anything under `apps/`.

When Phase 3 arrives, this directory is extracted via `git filter-repo --subdirectory-filter packages` into its own repo with full history preserved. Every decision made here — layering, naming, dependencies, test boundaries — should be one that still holds after that split.

## Source of truth

Read these before changing anything non-trivial. They are the specification; this file only captures the invariants Claude Code must not break while working inside `packages/`.

- `packages/docs/affiant-framework-specification.md` — full spec: layers, primitive types, interfaces, the seven normative rules (§6), the tool authoring guide (§7). Lives under `packages/` because it ships with the framework in the Phase 3 OSS split.
- `docs/architecture/phase-2-prd-affiant-framework-extraction.md` — the task-by-task extraction sequence from Meridian into framework packages (monorepo-level planning, stays at root)
- `docs/architecture/affiant-repo-architecture.md` — repo topology, the `packages/` vs `apps/` split, the conditional `ProjectReference`/`PackageReference` pattern, CI layout (monorepo-level, stays at root)
- `docs/architecture/phase-1-prd-production-ready-meridian.md` — what Meridian needed to become production-ready; useful context for understanding what's being extracted (monorepo-level, stays at root)

## The Seven Normative Rules

These are non-negotiable. Every framework change must conform. Full rationale and anti-patterns in framework spec §6.

1. **One system prompt per agent, immutable after initialization.** Framework code never mutates the system prompt at runtime.
2. **Dual-audience tool returns.** Every tool return is readable by both the LLM and the UI. Read tools return markdown + `[entity:id]` refs; write tools return Affidavits.
3. **Write tools never write.** Write-intent tools produce `WriteProposal` envelopes. The actual write happens only after `ReviewGate` confirmation, via the host's `IWriteExecutor`.
4. **Filters over prompts for determinism.** Context extraction, task inference, and review gating live in SK filters — never in prompt text.
5. **Graceful degradation on provider failure.** Primary LLM outage must fall back to secondary or a deterministic degraded mode. Never throw an unhandled exception from a chat completion call.
6. **`data-guide` contracts are UI-layer registrations.** The LLM discovers guidable elements through `IRouteRegistry`, never via DOM inspection or generated CSS selectors.
7. **Every Affidavit field carries provenance, no exceptions.** If unknown, tag it `ProvenanceSource.Empty`. Never omit.

## Layering invariant (read this twice)

The framework dependency graph is a DAG rooted at `Affiant.Abstractions`:

```
Affiant.Abstractions        (zero Affiant dependencies)
        ↑
Affiant.Core                (concrete services; references Abstractions)
        ↑
Affiant.SemanticKernel, Affiant.Docket, Affiant.EntityFramework,
Affiant.Policies, Affiant.Transport.SignalR
```

- **`Affiant.Abstractions`** holds all domain-agnostic primitive types (`ToolEnvelope`, `Affidavit`, `ProvenanceTag`, `ProvenanceChain`, `DocketEntry`, `TransportEvent`, etc.) AND all framework interfaces (`IChatSessionStore`, `IDocketStore`, `IStreamingTransport`, `IApprovalPolicy`, `IFieldMapper<T>`, `IWriteExecutor`, `IRouteRegistry`, `IIntentInterceptor`, etc.). It must never reference any other Affiant package. A host app that only needs to implement a contract should be able to reference this package alone.
- **`Affiant.Core`** holds concrete services (`ContextFabric`, `ReviewGate`, `ContextExtractor<T>` base class, `TaskInferenceStep`, `DeterministicShortCircuit`, `UiGuidanceBridge`, `AffiantTelemetry`) and references `Affiant.Abstractions`. Core never references any adapter package (no `ProjectReference` to `Affiant.Docket`, `Affiant.EntityFramework`, etc.) — it consumes them through interfaces injected via DI.
- **Adapter packages** (`Affiant.SemanticKernel`, `Affiant.Docket`, `Affiant.EntityFramework`, `Affiant.Policies`, `Affiant.Transport.SignalR`) reference `Affiant.Core` (which transitively pulls in `Abstractions`). None of them may reference each other.

If you find yourself wanting to add a `ProjectReference` that inverts this DAG, stop. Surface the coupling to the user and ask how to resolve it — don't paper over it.

This matches the standard `Microsoft.Extensions.*.Abstractions` / `Microsoft.Extensions.*` layering.

## Domain-agnostic code only

Nothing in `packages/` may reference Meridian's aviation domain (no `WorkOrder`, `Aircraft`, `Customer`, `FleetStatus`, etc.) or HR Portal's HR domain (no `LeaveRequest`, `Employee`, etc.). The `Affidavit.Fields[]` contract uses `string` field names and `object` values — that is the domain-agnostic boundary. If you're extracting a type from Meridian and it has any domain coupling, the coupling must be removed before it crosses into `packages/`.

Grep `packages/` for `WorkOrder`, `Aircraft`, `Meridian`, `HRPortal`, `LeaveRequest`, `Employee` before committing an extraction. Any hit is a bug.

## The `packages/` ↔ `apps/` boundary

- `packages/` never contains `ProjectReference` to anything under `apps/`. Structurally prevented — `<IsPackable>true</IsPackable>` is set in `packages/Directory.Build.props`, so a NuGet pack would fail on such a reference anyway, but also don't try.
- Host apps reference framework projects via the conditional `UseAffiantPackages` MSBuild property pattern documented in `affiant-repo-architecture.md`. Both branches must stay in sync: adding a new framework package means updating both the `ProjectReference` and `PackageReference` item groups in every host.
- When this subtree is eventually extracted via `git filter-repo`, everything in `packages/` must remain a self-contained buildable repo with its own `Affiant.slnx`, tests, and `Directory.Build.props`. Don't add anything that depends on the root-level monorepo being present.

## Target framework and C# conventions

- **Target framework**: `net10.0`. Do not introduce `<TargetFrameworks>` multi-targeting unless there's a concrete reason — Phase 3 may add `netstandard2.0` for broader reach, but not before.
- **Nullable reference types**: enabled globally. Don't disable per file.
- **Implicit usings**: enabled globally. Don't add implicit `using` directives that are already pulled in by the SDK.
- **File-scoped namespaces** (`namespace Affiant.Core.Services;`). Never block-scoped.
- **Records** for all DTOs, models, and immutable value types. Classes only for services with behavior and mutable state. `readonly record struct` for small value types that benefit from stack allocation.
- **Primary constructors** on services where all dependencies are captured by DI.
- **`[JsonDerivedType]`** on `ToolEnvelope` for polymorphic serialization, using the `type` discriminator. Matches SK's own `KernelContent` pattern.
- **Package IDs** must match the reserved names on nuget.org exactly: `Affiant.Core`, `Affiant.Abstractions`, `Affiant.SemanticKernel`, `Affiant.Docket`, `Affiant.EntityFramework`, `Affiant.Policies`, `Affiant.Transport.SignalR`. Version is shared across all packages via `packages/Directory.Build.props`.

## Build, pack, test

All framework work must leave both solutions buildable. Host apps use `<ProjectReference>` to framework projects, so any change here transitively affects them.

```bash
# Framework-only build
dotnet build packages/Affiant.slnx -c Release

# Pack to validate NuGet structure (no publish)
dotnet pack packages/Affiant.slnx -c Release -o ./nupkgs/

# Framework tests
dotnet test packages/Affiant.slnx -c Release

# Full cross-check: framework + every host
dotnet build packages/Affiant.slnx -c Release \
  && dotnet build apps/Meridian/Meridian.sln -c Release
```

Use xUnit for framework tests. Prefer `[Theory]` with a provider factory for tests that should run against multiple adapter implementations (e.g., the shared `Affiant.Docket.Tests` suite running against InMemory + SQLite + Postgres). Tests live under `packages/tests/`, one test project per src project.

## Extraction workflow (Phase 2)

Phase 2's extraction is a series of small, reversible moves. Follow this rhythm:

1. Pick one logical component (primitive type group, one interface, one service).
2. Move it from Meridian into the correct framework package.
3. Update namespaces, `using` statements, and any `ProjectReference` edges.
4. Run `dotnet build packages/Affiant.slnx && dotnet build apps/Meridian/Meridian.sln`. Both must succeed.
5. Commit with a message scoped to that one component.
6. Repeat.

If a single step touches more than ~10 files or breaks Meridian's runtime behavior, split it further. The blast radius of any single extraction commit should be small enough to revert cleanly. This is not the place for "big bang" refactors.

When moving a type, preserve behavior exactly — Phase 2 is a refactor, not a feature-adding phase. If you find a bug during extraction, note it and fix it in a separate commit so the refactor stays pure.

## What NOT to do

- **No comments explaining what code does.** Well-named identifiers carry that weight. Only add a comment when the *why* is non-obvious (a hidden constraint, a workaround for a specific bug, a subtle invariant that would surprise a reader).
- **No speculative abstractions.** Don't add a base class, interface, or generic parameter because "we might need it later." Three similar lines is better than a premature abstraction. The framework is small right now — resist the urge to future-proof.
- **No backwards-compatibility shims during Phase 2.** Pre-1.0 alpha means we break things cleanly. No `[Obsolete]` aliases, no re-exported types, no shim namespaces. If something is removed or renamed, the rename is the change.
- **No domain-specific imports.** Anything named after Meridian, HR Portal, aviation, HR, work orders, leave requests — none of it belongs in `packages/`.
- **No references to files outside `packages/`** (other than the root `Directory.Build.props` / `global.json` which are structural). The extracted repo must stand alone.
- **No secrets, connection strings, or API keys** in csproj, appsettings, or code. Framework code has no runtime configuration — it's all DI-driven.

## When in doubt

- If the PRD and this file disagree, the PRD wins — this file is a summary and will drift. But note the drift and surface it so the PRD and this file can be reconciled.
- If the framework spec and this file disagree, read both carefully and ask the user before acting. The spec is the design contract; this file is operational guidance.
- If extracting a type would violate the layering invariant, domain-agnostic invariant, or the seven rules — stop and ask. Don't try to "make it work."
