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

### Added — Area-1 field-provenance redesign: extraction fields, async resolvers, chain-merge fix, harness parity check

Implements the gate-approved Area-1 redesign
(`chancery docs/architecture-review/area-1-field-provenance-model.md`) in three parts, plus a
fix to a pre-existing chain-truncation defect ("V4") surfaced along the way.

- **Extraction fields (P1)** — `TaskInferenceField` gains an additive `bool Projected = true`
  (`src/Affiant.Abstractions/Interfaces/ITaskInferenceStrategy.cs`). Every existing construction
  site compiles unchanged and keeps its current (projected) behavior. Setting `Projected: false`
  declares an **extraction field**: the LLM is still asked to extract it (both
  `Affiant.SemanticKernel` and `Affiant.AgentFramework` inference ports already iterate
  `strategy.Fields` unconditionally — verified, no port changes needed) and it is still merged
  into `ContextFabric` by `TaskInferenceStep`, but `SchemaDrivenAffidavitProjection` no longer
  emits an `AffidavitField` for it. Instead its extracted value + `ProvenanceChain` (from
  `fabric.GetFieldChain`, may be absent if nothing was extracted yet) is collected into a new
  `ExtractionFacts` type (`src/Affiant.Abstractions/Models/ExtractionFacts.cs`) — exposed only to
  `IFieldResolver` implementations (see below) and never a member of `Affidavit`, never
  serialized toward reviewer clients. `Projected: false` combined with `Required: true` is
  invalid — an extraction fact never becomes a card field, so it can never gate the Evidence
  Card — and is rejected loudly with a precise message at
  `SchemaDrivenAffidavitProjection` construction time.
- **Async field resolvers (P2)** — new `IFieldResolver` contract
  (`src/Affiant.Abstractions/Interfaces/IFieldResolver.cs`):
  `string FieldName { get; }` and
  `Task<FieldResolution?> ResolveAsync(FieldResolutionContext ctx, CancellationToken ct)`, where
  `FieldResolutionContext` is `(IContextFabric Fabric, ExtractionFacts Facts)` and
  `FieldResolution` is `(object? Value, ProvenanceTag Tag)`
  (`src/Affiant.Abstractions/Models/FieldResolution.cs`). Registered via new
  `services.AddFieldResolver<TResolver>()` (mirrors the existing
  `AddDeterministicFieldSource<TSource>()` idiom, but **Scoped** rather than Singleton, so a
  resolver may take a DI-scoped dependency — e.g. a per-request lookup client — without becoming
  a captive dependency). `SchemaDrivenAffidavitProjection`'s per-field resolution precedence is
  now: `IFieldResolver` (first registered resolver for the field name whose `ResolveAsync`
  returns non-null wins) → legacy `IDeterministicFieldSource` (kept fully working) → raw
  `ContextFabric` chain → `ProvenanceTag.Empty`. Resolvers state their own derivation in
  `FieldResolution.Tag.Evidence` rather than hardcoding a tool/mechanism name that may not have
  run for a given resolution (documented on `IFieldResolver`'s XML docs with the convention
  example, e.g. `"Resolved from tail number N12345 (stated in conversation)"`).
- **`IDeterministicFieldSource` is now `[Obsolete]` (non-error)** — superseded by
  `IFieldResolver`: it is synchronous, returns only a bare `ProvenanceTag` (the value must
  already sit in `ContextFabric`'s entity), and cannot express a DI-scoped dependency because
  `AddDeterministicFieldSource` registers Singleton. Kept fully functional — existing hosts
  implementing it compile and behave exactly as before (including gaining the chain-merge fix
  below) — and will be removed in a future major version.
- **Chain-merge fix ("V4")** — both the resolver and legacy-source projection paths used to call
  `ProvenanceChain.From(tag)` unconditionally when a deterministic value won, silently discarding
  whatever `ProvenanceChain` already existed for that field (e.g. a conversational inference tag
  recorded earlier in the same turn). `SchemaDrivenAffidavitProjection` now follows
  `TaskInferenceStep.ExecuteAsync`'s existing merge idiom instead: the resolver/legacy tag is
  merged onto the prior chain via `ProvenanceChain.Merge` — following the same higher-confidence-
  wins, ties-broken-by-`ProvenanceSource`-ordinal rule already used there — so the prior tag
  survives in `ProvenanceChain.Prior` rather than being dropped, and only falls back to
  `ProvenanceChain.From(tag)` when no prior chain exists (structurally identical to the old
  behavior in that no-prior case). **Behavior change:** any test asserting that a deterministic
  win truncates prior chain history was necessarily updated as part of this fix — see
  `SchemaDrivenAffidavitProjectionAreaOneTests.Resolver_MergesOntoExistingChain_CurrentIsResolverTag_PriorContainsConversationTag`
  and `.LegacySource_MergesOntoExistingChain_PreservesPriorHistory` for the corrected behavior;
  no pre-existing test in this repo asserted the old truncating behavior directly, so none needed
  a behavior-reversing edit — the fix is additive-in-effect for existing suites.
- **`ComplianceHarness.AssertFieldSetParity` (P7, opt-in)** — new public API on
  `Affiant.Testing.ComplianceHarness.ComplianceHarness`:
  `FieldSetParityResult AssertFieldSetParity(ITaskInferenceStrategy strategy, IReadOnlyCollection<string> writeConsumedFieldNames)`.
  Reports (a) as `Errors`: any `Projected: true` card field the write path does not consume
  ("card field 'X' is not part of the write contract — make it an extraction field
  (Projected=false) or remove it"), and (b) as `Warnings` (non-failing, informational): a
  consumed name the strategy never declares. Extraction fields (`Projected: false`) are exempt
  from (a) — they exist to feed resolvers, not to be consumed verbatim by the write path. Does
  **not** run automatically inside `ComplianceHarness.Verify` — existing harness consumers stay
  green unchanged — a host calls it directly, typically with the domain write method's parameter
  names.

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

### Added — review lifecycle v2: real TTL, non-blocking filing, amendment preservation, resubmission, expiry events

Five related fixes to the review state machine (repo issues #7, #8, #9, #10, and framework
enabler for host issue `Sakwala/affiant-host-apps#25`):

- **TTL option becomes real (issue #7)** — `ReviewGate` now takes `AffiantCoreOptions` (the
  codebase's established option-injection idiom — a plain singleton, not `IOptions<T>`, matching
  how `AddAffiantCore()` already registers it) and uses `options.DefaultDocketTtl` for both the
  `DocketEntry.ExpiresAt` stamp and the blocking await's internal `CancelAfter` window. The
  hardcoded `DocketTimeoutMinutes = 10` const is deleted. Hosts that do not explicitly set
  `AffiantCoreOptions.DefaultDocketTtl` will see both the blocking-wait window and the
  `ExpiresAt` stamp move from the previous hardcoded 10 minutes to the option's existing 30-minute
  default — an observable behavior change on upgrade for any host relying on the old 10-minute
  value. A new `AffiantCoreOptions.DocketExpiryWarningWindow`
  option (default 2 minutes) configures the expiry-warning broadcast below.
- **Non-blocking filing API (framework enabler for `affiant-host-apps#25`)** — `ReviewGate.FileForReviewAsync(proposal, context, ct)`
  does everything the blocking path does up to the reviewer await — idempotency check, policy
  evaluation (StandingOrder/ReferralRequired short-circuits preserved, identical semantics),
  `DocketEntry` filing, Evidence Card broadcast — but registers no waiter and returns immediately:
  `ReviewFilingResult.RequiresReview(EntryId)` when a reviewer must act, or
  `ReviewFilingResult.Decided(Outcome)` when the review was already settled. `FileReviewAsync` is
  now a thin wrapper: it calls `FileForReviewAsync` and adds only the blocking await — a
  behavior-preserving refactor (every prior `ReviewGate` test still passes unmodified, aside from
  the two constructor call sites necessarily touched by the TTL-option change above).
- **Late amendments are persisted, not dropped (issue #8)** — `HandleDecisionAsync`'s restart path
  (entry not found / not Pending / already past `ExpiresAt`) used to silently discard the
  decision's `amendments` before returning `Expired`. It now persists non-empty amendments via the
  existing `IDocketStore.UpdateAmendmentsAsync` first. `ReviewOutcome.Expired` gains an additive
  `AmendmentsPreserved` flag (default `false`) so callers can tell late-preserved edits apart from
  a plain timeout with nothing to save. No store schema change — `AmendmentsJson` was already
  mapped by all three backends.
- **`ReviewGate.ResubmitAsync(expiredEntryId, ct)` (framework half of issue #9)** — loads the
  expired entry (`InvalidOperationException` if not found or not `Expired`, matching this
  codebase's existing not-found/wrong-state error idiom), files a **fresh** Pending entry (new
  `EntryId`, fresh TTL) cloning the original envelope/affidavit through the same
  `FileForReviewAsync` filing core, and broadcasts its Evidence Card with the original entry's
  persisted `Amendments` carried in a new optional `EvidenceCardRequest.PriorAmendments` field
  (additive, `null` default — existing broadcasts unchanged) so the new reviewer sees what was
  already agreed. **Lineage back to the expired entry is intentionally NOT persisted** — a
  `ResubmittedFromEntryId`-style column would require a schema migration across all three
  `IDocketStore` backends (InMemory/SQLite/Postgres via EF), which is out of scope here; both
  entry ids are logged together on resubmission so they can be correlated from application logs
  in the meantime. **Breaking (pre-1.0):** `ReviewGate.ReplayApprovalAsync` is deleted — grep
  confirmed it was dead code (no callers anywhere in this repo); its host-restart purpose is
  already served by `HandleDecisionAsync`'s restart path.
- **Expiry transport events (framework half of issue #10)** — `TransportEvent` gains
  `DocketExpiring` and `DocketExpired`, with payload records `DocketExpiringNotification {DocketId, ExpiresAt}`
  and `DocketExpiredNotification {DocketId}`. `DocketExpiryService` (Affiant.Docket) takes an
  **optional** `IStreamingTransport` (null-tolerant trailing constructor parameter — the Docket
  package must not hard-require a transport dependency) and, each 30-second tick: broadcasts
  `DocketExpired` for every entry it just bulk-expired, and `DocketExpiring` for every still-Pending
  entry whose `ExpiresAt` falls inside `DocketExpiryWarningWindow`. The warning set is re-queried
  every tick, so a warning re-emits on every tick an entry remains inside the window — **clients
  must treat repeated `DocketExpiring` notifications for the same docket id as idempotent** (e.g.
  key a UI countdown off the notification's `ExpiresAt` rather than counting notifications).
  `ReviewGate`'s own blocking-timeout path also broadcasts `DocketExpired` when it marks an entry
  expired, so both expiry sources (the background sweep and a live blocking await timing out)
  notify the UI the same way. `DocketEntry` already persisted `SessionId` end-to-end across all
  three stores before this change, so **no schema migration was needed** to target the broadcast —
  flagged here because the task briefing called this out as a possible gap; it turned out not to
  be one. `AddAffiantDocket()` now also `TryAddSingleton`s a default `AffiantCoreOptions` so
  `DocketExpiryService` resolves cleanly even for hosts that call `AddAffiantDocket()` without
  `AddAffiantCore()`; a host's real `AddAffiantCore()` registration always wins regardless of
  call order.

**Breaking (host-facing, if a host constructs these types directly instead of via DI):**
`ReviewGate`'s constructor gained an `AffiantCoreOptions options` parameter (inserted before the
trailing `ILogger`), and `DocketExpiryService`'s constructor gained a required
`AffiantCoreOptions options` parameter (inserted after `IServiceScopeFactory`) plus the optional
trailing `IStreamingTransport? transport`. Hosts that resolve both types through the DI container
(the pattern used everywhere in this repo and its own tests) are unaffected — `AffiantCoreOptions`
is already registered by `AddAffiantCore()`, and `AddAffiantDocket()` now guarantees a fallback
registration exists either way.

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
