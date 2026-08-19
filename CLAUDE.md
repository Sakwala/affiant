# CLAUDE.md — Affiant Framework

## What this is

This repository is the Affiant framework — the open-source .NET package set published to nuget.org. It provides "sworn provenance for every AI write": a Semantic Kernel-based layer with a two-filter context fabric, field-level provenance tracking, and a durable review queue (the Docket) with Evidence Cards and Referrals. Everything here must be reusable across any host application and must stay domain-agnostic.

The framework ships as nine NuGet packages, all sharing one version via the root `Directory.Build.props`:
`Affiant.Abstractions`, `Affiant.Core`, `Affiant.SemanticKernel`, `Affiant.AgentFramework`, `Affiant.Docket`, `Affiant.EntityFramework`, `Affiant.Policies`, `Affiant.Transport.SignalR`, `Affiant.Testing.ComplianceHarness`.

Host applications that consume this framework (e.g. the Meridian aviation-MRO copilot and the HR Portal) live in the separate private `Sakwala/affiant-host-apps` repository, which attaches this repo as a submodule at `./packages`. Nothing in this repo may reference host code.

## Source of truth

Read these before changing anything non-trivial. They are the specification; this file only captures the invariants that must not break.

- `docs/affiant-framework-specification.md` — full spec: layers, primitive types, interfaces, the seven normative rules (§6), the tool descriptor registry (§3.11), and the tool authoring guide (§7).
- `docs/tool-authoring-guide.md` — standalone extract of §7: the six plugin/filter/field-mapper/write-executor authoring patterns.

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
Affiant.SemanticKernel, Affiant.AgentFramework, Affiant.Docket, Affiant.EntityFramework,
Affiant.Policies, Affiant.Transport.SignalR
```

- **`Affiant.Abstractions`** holds all domain-agnostic primitive types (`ToolEnvelope`, `Affidavit`, `ProvenanceTag`, `ProvenanceChain`, `DocketEntry`, `TransportEvent`, etc.) AND all framework interfaces (`IChatSessionStore`, `IDocketStore`, `IStreamingTransport`, `IApprovalPolicy`, `IFieldMapper<T>`, `IWriteExecutor`, `IRouteRegistry`, `IIntentInterceptor`, etc.). It must never reference any other Affiant package. A host that only needs to implement a contract should be able to reference this package alone.
- **`Affiant.Core`** holds concrete services (`ContextFabric`, `ReviewGate`, `ContextExtractor<T>` base class, `TaskInferenceStep`, `DeterministicShortCircuit`, `UiGuidanceBridge`, `AffiantTelemetry`) and references `Affiant.Abstractions`. Core never references any adapter package — it consumes them through interfaces injected via DI.
- **Adapter packages** (`Affiant.SemanticKernel`, `Affiant.AgentFramework`, `Affiant.Docket`, `Affiant.EntityFramework`, `Affiant.Policies`, `Affiant.Transport.SignalR`) reference `Affiant.Core` (which transitively pulls in `Abstractions`). None of them may reference each other.

If you find yourself wanting to add a `ProjectReference` that inverts this DAG, stop. Surface the coupling to the user and ask how to resolve it — don't paper over it. This matches the standard `Microsoft.Extensions.*.Abstractions` / `Microsoft.Extensions.*` layering.

## Domain-agnostic code only

Nothing in this repo may reference a host's business domain (no `WorkOrder`, `Aircraft`, `Customer`, `FleetStatus`, `LeaveRequest`, `Employee`, etc.). The `Affidavit.Fields[]` contract uses `string` field names and `object` values — that is the domain-agnostic boundary. Any type with domain coupling does not belong here; the coupling must be removed before code crosses into the framework.

Grep for `WorkOrder`, `Aircraft`, `Meridian`, `HRPortal`, `LeaveRequest`, `Employee` before committing. Any hit is a bug.

## Target framework and C# conventions

- **Target framework**: `net10.0`. Do not introduce `<TargetFrameworks>` multi-targeting unless there's a concrete reason.
- **`LangVersion 12.0`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`** — all set globally in the root `Directory.Build.props`. Don't disable per file. A warning fails the build.
- **File-scoped namespaces** (`namespace Affiant.Core.Services;`). Never block-scoped.
- **Records** for all DTOs, models, and immutable value types. Classes only for services with behavior and mutable state. `readonly record struct` for small value types.
- **Primary constructors** on services where all dependencies are captured by DI.
- **`[JsonDerivedType]`** on `ToolEnvelope` for polymorphic serialization, using the `type` discriminator.
- **Package IDs** must match the reserved names on nuget.org exactly (the nine listed above under "What this is"). Version is shared across all packages via the root `Directory.Build.props`.

## Build, pack, test

```bash
# Build (implicit restore; TreatWarningsAsErrors is active — 0 warnings required)
dotnet build Affiant.slnx -c Release

# Test
dotnet test Affiant.slnx -c Release

# Pack to validate NuGet structure (no publish)
dotnet pack Affiant.slnx -c Release -o ./nupkgs/
```

`global.json` pins the SDK to `10.0.105` with `rollForward: latestPatch`. Use xUnit for tests. Prefer `[Theory]` with a provider factory for tests that should run against multiple adapter implementations (e.g. the shared Docket suite over InMemory + SQLite + Postgres). Tests live under `tests/`, one test project per src project.

## What NOT to do

- **No comments explaining what code does.** Well-named identifiers carry that weight. Only add a comment when the *why* is non-obvious (a hidden constraint, a workaround for a specific bug, a subtle invariant that would surprise a reader).
- **No speculative abstractions.** Don't add a base class, interface, or generic parameter because "we might need it later." The framework is small — resist the urge to future-proof.
- **No backwards-compatibility shims pre-1.0.** Alpha/beta means we break things cleanly. No `[Obsolete]` aliases, no re-exported types, no shim namespaces. If something is removed or renamed, the rename is the change.
- **No domain-specific imports.** Anything named after a host domain (aviation, HR, work orders, leave requests) does not belong here.
- **No secrets, connection strings, or API keys** in csproj, appsettings, or code. Framework code has no runtime configuration — it's all DI-driven.

## When in doubt

- If the framework spec and this file disagree, read both carefully and ask before acting. The spec is the design contract; this file is operational guidance and will drift.
- If a change would violate the layering invariant, the domain-agnostic invariant, or the seven rules — stop and ask. Don't try to "make it work."
