# Changelog

All notable changes to the Affiant framework are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Nine packages (`Affiant.Abstractions`, `Affiant.Core`, `Affiant.SemanticKernel`,
`Affiant.AgentFramework`, `Affiant.Docket`, `Affiant.EntityFramework`, `Affiant.Policies`,
`Affiant.Transport.SignalR`, `Affiant.Testing.ComplianceHarness`) are versioned in lockstep as of
2026-07-05; `Affiant.AgentFramework`'s NuGet ID reservation is pending, so it is not yet part of
any *published* release (`docs/proposals/affiant-maf-adapter.md` §9).

## [Unreleased]

### Added

- **`Affiant.AgentFramework`** — the Microsoft Agent Framework (MAF) interception backend, a
  peer of `Affiant.SemanticKernel` behind one shared, backend-neutral tool-invocation pipeline.
  `AffiantToolCatalog.FromType<T>()` reflects a tool type into `AIFunction`s and
  `AffiantToolDescriptor`s in one pass; `agent.WithAffiant(services, catalog)` is the single
  wiring call that attaches Affiant's provenance/inference/review-gate pipeline to a MAF
  `AIAgent` and returns the wrapped instance. Ships with a structural hosted-tool coverage audit
  (`AgentFrameworkOptions.AcknowledgeUncoveredTools`, `AgentFrameworkOptions.AllowUnauditableAgent`)
  that refuses by default rather than silently missing writes MAF's client middleware cannot
  see. See `docs/adapters/microsoft-agent-framework.md`. NuGet ID reservation pending — ships
  in-repo only until then.

### Changed — pre-1.0 clean break: backend-neutral tool-invocation pipeline

`Affiant.Core` previously took a direct `Microsoft.SemanticKernel` dependency and several of its
filters implemented SK's own filter interfaces, in violation of the framework's own L2 AC #4
constraint ("`Affiant.Core` must not take a direct SK dependency"). This is now fixed: all
interception logic (provenance tagging, task inference, review gating) is defined once,
backend-neutrally, in `Affiant.Core`, against a new `IToolInvocationFilter`/`ToolInvocationContext`
contract in `Affiant.Abstractions`. Both `Affiant.SemanticKernel` and the new
`Affiant.AgentFramework` are now thin translation bridges over that one pipeline. This is a
pre-1.0 clean break, not a deprecation — there is no compatibility shim:

- **Removed** `IToolInvocationCapture` (`Affiant.Abstractions`) — unused; superseded by the new
  neutral filter contract.
- **`IChatSessionStore`, `InferenceCompletionRequest`, `InferenceFixtureCase`** (`Affiant.Abstractions`)
  are retyped from Semantic Kernel's `ChatMessageContent`/`ChatHistory` to a new neutral
  `AffiantChatMessage` record. Each backend converts at its own edge
  (`SkMessageConversions` for SK, `MafMessageConversions` for MAF).
- **`InferenceTriggerFilter`, `ToolArgumentCaptureFilter`, `ReviewGateFilter`** moved from
  `Affiant.SemanticKernel.Filters` to `Affiant.Core.Filters`, and no longer implement SK's
  `IFunctionInvocationFilter`/`IAutoFunctionInvocationFilter` directly — they implement the
  neutral `IToolInvocationFilter`. Hosts that referenced these types by their old namespace
  directly (rather than only through `AddAffiantSemanticKernel()`/`AddAffiantInferenceOrchestration()`)
  must update the `using`.
- **`TaskInferenceRunner`** (`Affiant.Core`) no longer takes an SK `ChatHistory` parameter; it
  takes the neutral message list instead.
- **Added, `Affiant.SemanticKernel`**: `AffiantFunctionInvocationBridge` (implements SK
  `IFunctionInvocationFilter`) and `AffiantAutoFunctionInvocationBridge` (implements SK
  `IAutoFunctionInvocationFilter`) — the two concrete classes that now bridge SK's two-position
  filter split to the neutral pipeline. Hosts calling
  `AddAffiantSemanticKernel()`/`AddAffiantInferenceOrchestration()` see no change to their own DI
  call sites.
- **Moved, `Affiant.SemanticKernel`**: `SessionRehydrator`/`RehydrationResult` moved in from
  `Affiant.Core` (they build an SK `ChatHistory` and are host-wired, not framework-DI-registered —
  moving them removed the last SK dependency from `Affiant.Core`).
- **Docs**: `docs/affiant-framework-specification.md` §3.8, §3.12.3, §3.12.4, §4 (Package
  Mapping), and §5 (Framework Boundary Contract, new Seam 4) corrected/rewritten to describe this
  architecture; see those sections for full detail.

## [1.0.0-beta.1] — TBD *(planned; not yet published — the version flip from `alpha.1` happens with the publish step itself)*

First public release. The framework is a deterministic evidence layer for .NET agents:
every AI-proposed database write is a sworn, field-level `Affidavit` reviewed by a human
before it commits.

### Added

- **Eight co-versioned packages** targeting `net10.0`, arranged as a strict DAG rooted at
  `Affiant.Abstractions` (primitive types and interfaces), with `Affiant.Core` (concrete
  services) beneath five adapters — `Affiant.SemanticKernel`, `Affiant.Docket`,
  `Affiant.EntityFramework`, `Affiant.Policies`, `Affiant.Transport.SignalR` — plus
  `Affiant.Testing.ComplianceHarness`.
- **Field-level sworn provenance.** `Affidavit` / `AffidavitField` carry a `ProvenanceChain`
  and `PreviousValue` per field, an `AggregateConfidence`, and the seven-state
  `ProvenanceSource` determinism hierarchy (`UserStated` → `Empty`).
- **`ToolEnvelope`** discriminated return type for all tools — `ReadResult`, `WriteProposal`,
  and `ToolError` — enforcing dual-audience returns and the "write tools never write" rule.
- **Review pipeline.** `ReviewGate` state machine, the durable Docket review queue, Evidence
  Card request/response round-trip, and standing-order / referral / reviewer-confirmation
  approval policies.
- **L2 structured-output inference orchestration** — `ITaskInferenceStrategy` field schemas,
  the task-inference merge step, and per-entity affidavit projection.
- **Tool Descriptor Registry** — declarative write-intent registration via the
  `[AffiantWriteTool]` attribute and `AddAffiantTool<TStrategy>()`.
- **`Affiant.Testing.ComplianceHarness`** — `ComplianceHarness.Verify(...)` proves every
  registered write strategy has a paired fixture asserting substantive provenance, for
  adopters' own CI pipelines.
- Persistence backends for the Docket and sessions: in-memory, SQLite, and PostgreSQL.
- SignalR streaming transport and Evidence Card hub.
- Apache-2.0 licence with `LICENSE` and `NOTICE`; the tool-authoring guide and framework
  specification ship under `docs/`.

### Notes

- Validated by two independent first-party host applications. This is a **beta**: the
  invariant (every field carries provenance) is stable; the public API may change before
  1.0.0 GA.
- All pre-`beta.1` versions (`1.0.0-alpha.1` and earlier) were internal only and were never
  published to nuget.org.

[Unreleased]: https://github.com/affiant-dev/affiant/compare/v1.0.0-beta.1...HEAD
[1.0.0-beta.1]: https://github.com/affiant-dev/affiant/releases/tag/v1.0.0-beta.1
