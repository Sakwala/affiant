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

- **Affidavit field metadata (framework half of issue #11, flagship decision D6)** —
  `AffidavitField` gains three additive members, all with defaults so every existing
  construction site compiles unchanged: `Kind` (`string`, one of the new
  `AffidavitFieldKind` constants `"text"` | `"number"` | `"date"` | `"enum"`, default
  `"text"`), `AllowedValues` (`IReadOnlyList<string>?`, default `null`), and `Pattern`
  (`string?`, default `null`). `Kind` is deliberately a plain string rather than an enum
  type — an enum here would need a JSON converter, and converter behavior has drifted
  between the SignalR and plain-JSON transports before; the values live as string
  constants on `AffidavitFieldKind` in one place
  (`src/Affiant.Abstractions/Models/Affidavit.cs`) so every producer/consumer references
  the same literals. `TaskInferenceField` gains an optional `Format` (`string?`, default
  `null`, e.g. `"date"`) — the explicit signal for date-typed fields, since deriving
  "date" from a regex `Pattern` would be guesswork. `SchemaDrivenAffidavitProjection.Project`
  derives `Kind`/`AllowedValues` per field: a non-null `TaskInferenceField.Enum` wins
  (`Kind` `"enum"`, `AllowedValues` from `Enum`); else a `JsonType` of `"number"` or
  `"integer"` maps to `Kind` `"number"`; else `Format == "date"` maps to `Kind` `"date"`;
  else `Kind` defaults to `"text"`. `Pattern` is forwarded from
  `TaskInferenceField.Pattern` unconditionally, regardless of `Kind`. On the wire this
  travels through the SignalR transport's default `JsonHubProtocol` (camelCase), so a
  reviewer UI reads `kind` (lowercase string), `allowedValues` (array), and `pattern`
  off the `EvidenceCardRequest.affidavit.fields[]` payload — pinned by
  `SignalRTransportContractTests.EvidenceCardRequest_AffidavitFieldMetadata_SerializesToPinnedWireShape`.
  Host applications adopt `Format` and render `Kind`/`AllowedValues`/`Pattern` in their
  own review UI later — this change is framework-only and does not touch host repos.

- **Evidence Card amendment round-trip (framework half of issue #6)** — `EvidenceCardResponse`
  gains a trailing `IReadOnlyDictionary<string, object?>? Amendments` param carrying the fields a
  reviewer edited before approving (`null` value = the reviewer explicitly cleared that field).
  `IDocketStore` gains `UpdateAmendmentsAsync(entryId, amendments, ct)`, implemented by
  `InMemoryDocketStore`, `SqliteDocketStore`, and `PostgresDocketStore` (no EF model/migration
  change — `DocketEntryEntity.AmendmentsJson` was already mapped). `ReviewGate.FileReviewAsync`
  persists `EvidenceCardResponse.Amendments` onto the `DocketEntry` immediately after the approval
  transition wins the double-submit race; `ReviewGate.HandleDecisionAsync` gains a matching
  `amendments` parameter, threading it into the live-waiter `EvidenceCardResponse` and persisting
  it directly on the host-restart replay path. **Breaking (pre-1.0):** `DocketEntry.Amendments`
  widens from `IReadOnlyDictionary<string, object>?` to `IReadOnlyDictionary<string, object?>?` so
  an explicit reviewer-cleared field survives the round-trip distinguishably from an unamended
  field — update any code pattern-matching on the old type. Framework responsibility ends at
  persistence: appending `ProvenanceTag.FromUser` (Rule 7 UserStated tag — already exists; the
  issue's `FromUserStated` name doesn't) to each amended field's `ProvenanceChain` before the
  write reaches the domain store is the host's `IWriteExecutor` overlay's job — see
  `IWriteExecutor.ExecuteAsync`, which already accepts the amendments dictionary for exactly that
  purpose. UI, SignalR hub signature (`ApproveAction`/`useSignalR.approveAction`), and the
  executor overlay itself are host-apps follow-through, tracked against issue #6.
  
  **Breaking (host-facing, latent until package re-pin):** Host applications using this framework
  must not bump the `packages` submodule pointer past commit `331a8ea` until both of the following
  host-side updates land:
  
  - **`ReviewGate.HandleDecisionAsync` signature change** — gained an `amendments` parameter
    before the trailing `CancellationToken`. Existing host calls at `affiant-host-apps:ChatHub.cs:137`
    and `affiant-host-apps:ChatHub.cs:150` pass `entryId` and `decision` positionally without
    the new `amendments` argument, causing **CS1503 hard compile error** once host-apps re-pins.
    **Fix:** update both call sites to pass `amendments` (available from the `EvidenceCardResponse`
    or context) as the third positional argument before `ct`.
  
  - **`DocketEntry.Amendments` type widening breaks host's `GetAmendmentString` signature** —
    the host's `WorkOrderExecutor.GetAmendmentString(IReadOnlyDictionary<string, object>?, string)`
    at `affiant-host-apps:WorkOrderExecutor.cs:53` and `:108` expects `object` (not `object?`)
    values in the amendments dictionary. The framework now carries `object?` to distinguish
    explicit field clears from unamended fields. **CS8619 compile error** (object to object?
    assignment) with `TreatWarningsAsErrors` enabled. **Fix:** widen the parameter type to
    accept `IReadOnlyDictionary<string, object?, string>` instead.

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

### Fixed — conversation-scoped context fabric

- **`ContextFabric` / `IContextFabric` are now registered `Scoped`** by `AddAffiantCore()` (was
  `Singleton`). The context fabric is a conversation-scoped store; a singleton shared one
  un-namespaced entity/provenance store across every concurrent conversation, so values bled between
  conversations through shared keys and the per-session `Clear()` could race a concurrent
  conversation's provenance to `ProvenanceTag.Empty` mid-projection. `TaskInferenceStep` moves to
  `Scoped` with it (it captures the fabric). **Hosts must not re-register the fabric as a singleton**,
  and any service that captures it must be `Scoped`/`Transient` (tool-authoring guide §4.1).
- **`ToolInvocationPipeline` now runs filters (and the fabric) in the caller's ambient service
  scope** instead of a fresh detached root scope it created per invocation. `RunAsync` takes an
  optional ambient `IServiceProvider`; the SK bridges and `ManualToolInvoker` pass `kernel.Services`
  so a turn's invocation and completion stages share one fabric, and the MAF middleware passes
  `AIFunctionArguments.Services` (falling back to a pipeline-owned per-invocation scope). Concurrent
  turns resolve distinct scopes and stay isolated.
- **MAF now threads a real conversation id** onto the neutral context from
  `FunctionInvocationContext.Options.ConversationId`, giving `InferenceTriggerFilter` a genuinely
  per-conversation idempotency namespace instead of collapsing to a fabric-instance hash.
- **`ContextFabric` guards its internal dictionaries with a monitor** so read-modify-write stays
  atomic under concurrent access — belt-and-suspenders behind the scoping isolation.

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
