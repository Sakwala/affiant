# Changelog

All notable changes to the Affiant framework are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Ten packages (`Affiant.Abstractions`, `Affiant.Core`, `Affiant.SemanticKernel`,
`Affiant.AgentFramework`, `Affiant.Extensions.AI`, `Affiant.Docket`, `Affiant.EntityFramework`,
`Affiant.Policies`, `Affiant.Transport.SignalR`, `Affiant.Testing.ComplianceHarness`) are versioned
in lockstep as of 2026-07-05 (`Affiant.Extensions.AI` joined the set 2026-08-20). All ten NuGet IDs,
plus the bare `Affiant` meta-ID, are reserved on nuget.org (the last two, `Affiant.AgentFramework`
and `Affiant.Extensions.AI`, verified live 2026-07-31 and 2026-08-20 respectively).

## [Unreleased]

_Nothing yet — changes after `v1.0.0-beta.1.1` accumulate here._

## [1.0.0-beta.1.1] — unreleased

### Fixed

- **A Standing Order written to the documented contract could never auto-approve.**
  `StandingOrderBase` defaulted its `RiskThreshold` to `RiskLevel.Low` (1) and auto-approved only
  when the computed score was at or below it, while the risk formula `AddAffiantPolicies()`
  registered for every host returned `Medium` (2) or `High` (3) on every path — over-50 `Value`
  field → High, any other `Value` → Medium, no `Value` field → Medium. Nothing scored `Low`, so a
  subclass that implemented `MatchesAsync` and changed nothing else always fell through to reviewer
  confirmation.
- **New semantics.** `RiskThreshold` is now `int?` and defaults to `null`, meaning *no risk
  ceiling*: matching the conditions is the whole test, and such a Standing Order needs no risk
  calculator at all. Declaring a threshold opts into scoring — the framework still owns the
  `score <= threshold` comparison, the host owns the score.
- **Fail closed on misconfiguration.** A Standing Order that declares a `RiskThreshold` with no
  `RiskScoreCalculatorBase` registered now throws `InvalidOperationException` naming
  `SetRiskScoreCalculator<T>()`. It fails on the policy's first evaluation, before any write is
  auto-approved, never silently — rather than deferring every write it was written to approve.

### Added

- `AffiantPolicies.ValidateStandingOrders(IServiceProvider)` — an optional boot-time check. It
  resolves every registered `IApprovalPolicy` in a throwaway scope and runs each Standing Order's
  risk-configuration check, turning a misconfiguration into a startup failure rather than a
  first-request one. It evaluates no Affidavit and approves nothing.

### Changed

- `RiskScoreCalculatorBase.ComputeAsync` is **abstract**. There is no framework scoring formula:
  what counts as risk is a property of the host's domain. `ClassifyScore` and the `RiskLevel` enum
  are unchanged.
- `AddAffiantPolicies()` registers an internal placeholder `RiskScoreCalculatorBase` when the host
  registers none. It carries no formula and no risk floor — every call to it throws, naming
  `SetRiskScoreCalculator<T>()`. It exists so that a Standing Order whose constructor takes
  `RiskScoreCalculatorBase` as a *required* dependency — the shape every `1.0.0-beta.1` order that
  declared a `RiskThreshold` was forced into — still resolves, and so sees the actionable message
  rather than the container's own "Unable to resolve service for type 'RiskScoreCalculatorBase'".
  It is registered with `TryAdd`, so a calculator the host registers always wins.
- `StandingOrderBase`'s risk calculator is an optional constructor dependency
  (`RiskScoreCalculatorBase? riskScorer = null`), and the protected `RiskScorer` field is nullable.
- `StandingOrderBase.RiskThreshold` is `int?` (was `int`).

### Removed

- `DefaultRiskScoreCalculator`, and its automatic registration inside `AddAffiantPolicies()`.
  `AddAffiantPolicies()` no longer registers any scoring formula — only the throwing placeholder
  described above.

### Upgrade note

- A host that relied on the stock formula — over-50 `Value` field → High, otherwise Medium —
  registers its own calculator: subclass `RiskScoreCalculatorBase`, implement `ComputeAsync`, and
  pass it to `SetRiskScoreCalculator<T>()` inside `AddAffiantPolicies(...)`.
- A host with a Standing Order that overrides `RiskThreshold` must register a calculator, or that
  policy throws on its first evaluation, before any write is auto-approved, naming
  `SetRiskScoreCalculator<T>()`. Call `AffiantPolicies.ValidateStandingOrders(app.Services)` after
  `Build()` to hit the same failure at startup instead. Changing the override's type from `int` to
  `int?` is required to compile.
- An order that took the calculator as a required constructor parameter — the shape beta.1's base
  constructor forced — keeps working unchanged: it resolves against the placeholder and, if it
  declares a `RiskThreshold`, reports the missing registration itself. Widening the parameter to
  `RiskScoreCalculatorBase? scorer = null` is optional.
- A host whose Standing Orders never overrode `RiskThreshold` needs no calculator and no code
  change — but note the behaviour change: those orders now auto-approve on the match, which is what
  they were always written to do.
- These are declared breaking changes against `1.0.0-beta.1`, permitted by the prerelease-stability
  policy, and recorded in `src/Affiant.Policies/CompatibilitySuppressions.xml`.

## [1.0.0-beta.1] — 2026-08-23

First public release. The framework is a deterministic evidence layer for .NET agents:
every AI-proposed database write is a sworn, field-level `Affidavit` reviewed by a human
before it commits.

### Release summary

- **Ten co-versioned packages** targeting `net10.0`, arranged as a strict DAG rooted at
  `Affiant.Abstractions` (primitive types and interfaces), with `Affiant.Core` (concrete
  services) beneath seven adapters — `Affiant.SemanticKernel`, `Affiant.AgentFramework`,
  `Affiant.Extensions.AI`, `Affiant.Docket`, `Affiant.EntityFramework`, `Affiant.Policies`,
  `Affiant.Transport.SignalR` — plus `Affiant.Testing.ComplianceHarness`.
- **Three interception backends.** Backend-neutral tool interception with thin bridges for
  Semantic Kernel, the Microsoft Agent Framework, and Microsoft.Extensions.AI — covering
  locally-invoked tools only (hosted/server-side tools are out of scope, stated honestly on
  every doc surface).
- **Field-level sworn provenance.** `Affidavit` / `AffidavitField` carry a `ProvenanceChain`
  and `PreviousValue` per field, an `AggregateConfidence`, and the seven-state
  `ProvenanceSource` determinism hierarchy (`UserStated` → `Empty`).
- **`ToolEnvelope`** discriminated return type for all tools — `ReadResult`, `WriteProposal`,
  and `ToolError` — enforcing dual-audience returns and the "write tools never write" rule.
- **Review pipeline.** `ReviewGate` state machine, the durable Docket review queue, Evidence
  Card request/response round-trip with reviewer amendments, and standing-order / referral /
  reviewer-confirmation approval policies.
- **L2 structured-output inference orchestration** — `ITaskInferenceStrategy` field schemas,
  the task-inference merge step, and per-entity affidavit projection.
- **Tool Descriptor Registry** — declarative write-intent registration via the
  `[AffiantWriteTool]` attribute and `AddAffiantTool<TStrategy>()`.
- **`Affiant.Testing.ComplianceHarness`** — `ComplianceHarness.Verify(...)` proves every
  registered write strategy has a paired fixture asserting substantive provenance, for
  adopters' own CI pipelines — plus the cross-backend compliance parity suite gating all
  three bridges against every compliance fixture.
- Persistence backends for the Docket and sessions: in-memory, SQLite, and PostgreSQL.
- SignalR streaming transport and Evidence Card hub.
- Apache-2.0 licence with `LICENSE` and `NOTICE`; the tool-authoring guide, the Microsoft
  Agent Framework adapter guide, and the framework specification ship under `docs/` (the
  Semantic Kernel and Microsoft.Extensions.AI bridges are documented in their package
  READMEs).

### Notes

- Validated by two independent first-party host applications (one on the Microsoft Agent
  Framework bridge, one on the Microsoft.Extensions.AI bridge; the Semantic Kernel bridge is
  gated by the same cross-backend parity suite). This is a **beta**: the invariant (every
  field carries provenance) is stable; the public API may change before 1.0.0 GA.
- All pre-`beta.1` versions were internal only: `1.0.0-alpha.1` and earlier were never
  published, and the `0.0.x-preview` versions on nuget.org are empty name-reservation stubs,
  not usable releases.
- The engineering log below records the full pre-release history, newest first.

### Added — `Affiant.Extensions.AI`: the Microsoft.Extensions.AI adapter, a third interception backend

A new package, `Affiant.Extensions.AI`, bridges Affiant's backend-neutral tool-invocation pipeline
to [Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI) (`IChatClient`
+ `FunctionInvokingChatClient` + `AIFunction`, pinned to the verified-stable `10.9.0`) — the shared
abstraction layer both Semantic Kernel and the Microsoft Agent Framework (MAF) sit on top of. A host
built directly on Microsoft.Extensions.AI, with neither SK nor MAF, now gets the same provenance
tagging, task inference and review-gate behavior `Affiant.SemanticKernel` and `Affiant.AgentFramework`
already provide. Design brief:
`docs/overnight-mission-2026-08-20/meai-adapter-design.md` in the `affiant-chancery` repo.

- **Interception seam**: each `AIFunction` is wrapped in an `AffiantDelegatingAIFunction` — a
  `DelegatingAIFunction` that runs the neutral pipeline and reads/writes
  `FunctionInvokingChatClient.CurrentContext`, the same `AsyncLocal` mechanism MAF's own
  function-invocation middleware uses internally. This is deliberately not the
  `FunctionInvokingChatClient.FunctionInvoker` delegate property, which silently no-ops if a host
  forgets to set it; the wrapper *is* the function, so even a custom loop calling
  `AIFunction.InvokeAsync` directly still passes through Affiant.
- **`Terminate` propagation is proven, not assumed.** The first commit on this feature is a
  fail-first integration test (real `FunctionInvokingChatClient`, fake `IChatClient`) proving that
  mutating `FunctionInvokingChatClient.CurrentContext.Terminate` from inside a wrapped `AIFunction`
  reaches the loop's own termination check.
- **`WithAffiant(this ChatOptions, IServiceProvider, AffiantToolCatalog)`** is the one supported way
  to wire this adapter: it audits the tool list for provider-hosted tools Affiant cannot see
  (`HostedWebSearchTool` and friends — throws at wire-up unless acknowledged via
  `ExtensionsAIOptions.AcknowledgeUncoveredTools`), wraps every client-invoked `AIFunction`, and
  guards against double-wrapping.
- **The double-wrap guard has two halves, because one is structurally not enough.** At wire-up, a
  marker interface (`IAffiantWrappedFunction`) makes `WithAffiant` throw with an actionable message
  when it is handed tools this adapter already governs. That check is a top-level type test, so
  anything layered over an Affiant wrapper hides it — a host's own `DelegatingAIFunction`
  (telemetry, retry, redaction, argument coercion), or `Affiant.AgentFramework`, which rewrites
  `ChatOptions.Tools` with its own private wrapper type after this adapter's wire-up has run. So
  `AffiantDelegatingAIFunction` also refuses at *invoke* time: an ambient record of the onion in
  flight, tagged with the `FunctionInvocationContext` it belongs to, makes a nested wrapper throw
  rather than run the non-idempotent pipeline a second time (which would double-tag provenance, fire
  task inference twice, and file the same write proposal onto the docket twice). Reference equality
  on that context is what keeps the guard from over-firing: a tool body that starts its *own*
  governed sub-agent gets a fresh context from that sub-agent's `FunctionInvokingChatClient`, so its
  onion still runs. One Affiant adapter per tool catalog / chat-client pipeline is now enforced in
  every nesting shape, not merely documented.
- **Set `ChatOptions.ConversationId`; omitting it silently degrades task inference.** Affiant dedups
  inference per (conversation, tool, turn), and with no conversation id the key falls back to the
  identity of the conversation-state object (`IContextFabric`). At this seam that object is
  process-global — `FunctionInvokingChatClient` supplies the provider the `ChatClientBuilder` was
  built from (the application root) rather than a per-conversation scope — so every conversation
  collapses onto one key and the second and later ones skip write-tool inference with no exception
  and no warning. The limitation is framework-wide (`Affiant.SemanticKernel` and
  `Affiant.AgentFramework` source their ambient provider identically); the per-turn-scope fix is
  deferred to its own wave. Both the failure and the one-line mitigation are pinned by
  `ConversationScopeBleedAtTheSeamTests`, and stated in the package README, `WithAffiant`'s XML docs
  and `AffiantDelegatingAIFunction`'s.
- **Shared code is copied, not referenced.** `AffiantToolCatalog` and the structured-output
  inference port (`ExtensionsAIInferenceCompletionPort`) are the same code `Affiant.AgentFramework`
  already carries at the Microsoft.Extensions.AI level — copied here (each file's header names its
  source) rather than pulled in via `ProjectReference`, because inverting `Affiant.AgentFramework`
  onto `Affiant.Extensions.AI` (the architecturally correct long-term shape, since MAF itself sits on
  Microsoft.Extensions.AI) re-shapes a shipped adapter and amends the no-adapter-to-adapter-reference
  layering invariant (`CLAUDE.md`, "Layering invariant") — deferred past beta by design, tracked in
  a filed consolidation issue (Sakwala/affiant, "Invert Affiant.AgentFramework onto
  Affiant.Extensions.AI post-beta").
- **Provider-neutral by construction**: this package references `Microsoft.Extensions.AI` only —
  never `Microsoft.Agents.AI`, never a concrete provider client (OpenAI, Azure, Gemini, ...). Which
  `IChatClient` a host brings is the host's business.
- Ships to the same Area-8 standard as every other package from birth: nupkg README +
  `PackageReadmeFile`, XML docs, `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`,
  `EnablePackageValidation`, Sakwala metadata (all inherited from the shared
  `Directory.Build.props`/`Directory.Build.targets` gates, same as the other nine packages).
- `Affiant.Testing.ComplianceHarness`'s cross-backend parity suite now runs against three backends
  (SK, MAF, Extensions.AI) via one added `yield return` in
  `InferenceCompletionPortProviderFactory` plus a descriptor-parity test mirroring
  `CatalogReflectionParityTests`.
- **This PR bumps every package-count gate from nine to ten**: `ci.yml`'s produced-package check,
  `CLAUDE.md`'s package list, this header, the README's "which packages" table and package table, and
  the framework specification's package mapping — see each for the updated list.

### Added — Area-8 P4: public API baselines and package validation

Every packable project now carries `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` and
references `Microsoft.CodeAnalysis.PublicApiAnalyzers` (centrally, from `Directory.Build.targets`).
The whole current surface — 1,768 entries across the nine packages, post-P2/P3 — is declared in the
unshipped files; the shipped files are empty because nothing has been published yet, which is the
tool's convention for a prerelease surface. Because `TreatWarningsAsErrors` is on, an undeclared
public member (`RS0016`) or a declared-but-deleted one (`RS0017`) **fails the build**, so CI's
existing `dotnet build -c Release` step enforces API drift with no workflow change: an API change now
has to show up in the diff as an API change.

Baselines are nullable-annotated (`#nullable enable` heads each shipped file), so a nullability change
counts as an API change too. `RS0041` is scoped off for `src/Affiant.EntityFramework/Migrations/*.cs`
in a new `.editorconfig` — EF Core scaffolds migrations with nullable disabled, and that generated
code is never hand-edited.

`EnablePackageValidation` is on for all nine packages. `PackageValidationBaselineVersion` is
deliberately unset until there is a published version to diff against.

### Added — Area-8 P4: missing `ReviewGate` wiring now fails the host at startup

`AddAffiantCore()` registers `ReviewGate`, which resolves `IStreamingTransport` and `IDocketStore`
lazily. A host that forgot `Affiant.Transport.SignalR` or a Docket backend therefore started, served
traffic and held a normal conversation with no error at all — the gap surfaced only when a tool first
produced a `WriteProposal` and `ReviewGateFilter` tried to file it, mid-conversation, at the one
moment provenance was supposed to be captured. That inverted the loudness rule the repo applies
elsewhere (the MAF adapter's hosted-tool audit refuses at wire-up; `Affiant.SemanticKernel`'s
`AffiantStartupValidator` refuses at startup).

`AddAffiantCore()` now inserts `Affiant.Core.Validation.AffiantWireUpValidator` as the first
`IHostedService`. At startup it asks the container (via `IServiceProviderIsService` — presence, not
resolution, since `IDocketStore` is Scoped on both SQL backends) whether each contract is registered
by *any* package, and throws `AffiantStartupException` naming every missing contract together with
the call and package that supplies it. Registration order between `AddAffiantCore`,
`AddAffiantDocket`, `AddAffiantEntityFramework` and `AddAffiantSignalR` is irrelevant to it — which is
why the check is a startup hosted service rather than eager validation inside `AddAffiantCore` (both
reference hosts register these packages in different orders, and half of those correct orders would
fail an eager check).

`AffiantCoreOptions.AcknowledgeMissingReviewWiring = true` downgrades the throw to one startup
warning per missing contract, for a host that deliberately runs Affiant's read/inference half with no
review loop. `Affiant.Core` gains one abstractions-only package reference,
`Microsoft.Extensions.Hosting.Abstractions` (part of the ASP.NET Core shared framework).

### Changed — Area-8 P3: the SQL-backed `IDocketStore` implementations move to `Affiant.EntityFramework` (affiant#35)

**Breaking for anyone calling `AddAffiantDocket(d => d.UseSqlite(...))` or `d.UsePostgres(...)`, and
for anyone naming `SqliteDocketStore`/`PostgresDocketStore` directly — whether fully qualified as
`Affiant.Docket.Stores.SqliteDocketStore` or by simple name under a `using Affiant.Docket.Stores;`,
which is the more common shape and breaks the same way. Free to
do exactly now: there are no published consumers (first publish is `1.0.0-beta.1`), and the repo's
"no backwards-compatibility shims pre-1.0" rule means the break is clean rather than layered.**

`Affiant.Docket` carried a `ProjectReference` onto `Affiant.EntityFramework` — the only
adapter-to-adapter edge in the graph, and one the repo's own layering invariant (`CLAUDE.md`,
"Layering invariant") forbids in emphatic terms. It existed for exactly two classes:
`SqliteDocketStore` and `PostgresDocketStore` both take `AffiantDbContext` as a constructor
dependency. Everything else those two need — `DocketEntryEntity`, `DocketEntityConfiguration`, the
`AddDocketEntries` and `AddResubmissionLineage` migrations — already lived in
`Affiant.EntityFramework`, and that package already implements the sibling contract
`IChatSessionStore` this exact way. Docket was the outlier.

- Both classes moved to `Affiant.EntityFramework.Stores` (namespace changed accordingly). Their
  implementations are unchanged, line for line.
- `AddAffiantEntityFramework(ef => ef.UsePostgres(...) / ef.UseSqlite(...))` now registers that
  provider's `IDocketStore` alongside its `IChatSessionStore`, Scoped, exactly as before.
  `ef.UseInMemory()` deliberately registers no `IDocketStore` — the in-memory implementation belongs
  to `Affiant.Docket`.
- `DocketOptions.UseSqlite(string)`, `.UsePostgres(string)` and the internal `ConnectionString`
  property are **deleted**. `DocketOptions.UseInMemory()` remains and is now the only selection.
- `AddAffiantDocket`'s `configure` parameter became optional, and the method no longer throws when no
  store is selected: for a SQL-backed host, "no selection" is now the correct call shape, since
  `AddAffiantDocket()` is still required for the backend-neutral `DocketExpiryService`. The loudness
  the old throw provided did not disappear — it moved to `AddAffiantCore`'s new startup wire-up
  validator (see the P4 entry below), which can see the whole composition root and therefore catches
  a host that registered *no* `IDocketStore` anywhere, regardless of registration order.
- `Affiant.Docket`'s `ProjectReference` to `Affiant.EntityFramework` is gone, restoring the declared
  DAG exactly. Installing `Affiant.Docket` no longer drags `Microsoft.EntityFrameworkCore`,
  `.Relational`, `.Sqlite` and `Npgsql.EntityFrameworkCore.PostgreSQL` onto a host that wants only
  `InMemoryDocketStore`.

**Host migration:** replace `AddAffiantDocket(d => d.UseSqlite(cs))` with
`AddAffiantEntityFramework(ef => ef.UseSqlite(cs))` (which every SQL-backed host already calls for
`IChatSessionStore`) plus a bare `AddAffiantDocket()`. No new package reference is needed — a host
using a SQL Docket already referenced `Affiant.EntityFramework`.

### Changed — Area-8 P2: eight data types move out of `Affiant.Abstractions.Interfaces` into `.Models`

**Source-breaking for anyone with `using Affiant.Abstractions.Interfaces;` who referenced these
unqualified without also importing `.Models`. Binary-clean and behavior-identical; no member
signature changed as part of this move.** `ChatSession`, `ConversationContext`, `ReviewRequirement`,
`ReviewResponse` (and its `ReviewGranted` / `ReviewDenied` / `ReviewExpired` cases) and
`TaskInferenceField` are plain data, but each was hand-added next to the interface that first needed
it. The package's own folder convention is "Interfaces = contracts, Models = data", and the drift
meant a `using Affiant.Abstractions.Models;` alone did not get you `ReviewRequirement` or
`ChatSession` for no principled reason. Pre-1.0 is the last cheap moment: after beta, every host
implementing `IApprovalPolicy` or `IChatSessionStore` has the old namespace baked into shipped code.
Fix at a call site is a `using` addition, nothing more.

### Changed — Area-8 P2: every amendments dictionary is now `IReadOnlyDictionary<string, object?>`

Three divergent shapes existed for the same "reviewer edits to a proposed write" payload. This is
not cosmetic: a non-nullable value type cannot represent "set this field to null", which is why a
host consuming the old `IWriteExecutor` contract had to filter null-valued entries out before
calling it, silently dropping explicit clear-field amendments.

- `IWriteExecutor.ExecuteAsync`'s `amendments` parameter: `Dictionary<string, object>?` →
  `IReadOnlyDictionary<string, object?>?`. **Breaking for implementers** (`Dictionary<,>` is an
  invariant concrete type). Update the signature; a key present with a `null` value now means
  "clear this field", distinct from the key being absent.
- `ReviewContext.Amendments` and `ReviewGranted.Amendments`: widened to the same shape. Non-breaking
  for producers — `IReadOnlyDictionary<,>` is covariant in its value type, so existing construction
  sites compile unchanged.

`DocketEntry.Amendments`, `IDocketStore.UpdateAmendmentsAsync`, `EvidenceCardRequest.PriorAmendments`
and `EvidenceCardResponse.Amendments` already used this shape and are unaffected.

### Changed — Area-8 P2: `RiskScoreCalculator` renamed to `RiskScoreCalculatorBase`

**Breaking for any host that subclassed it** (none known). It is an abstract extensibility base with
the same shape as its siblings `ReferralRuleBase` and `StandingOrderBase`, and was the only one of
the three whose name did not say so. `SetRiskScoreCalculator<T>()` keeps its name — it names the
role being set, not the base type.

### Removed — Area-8 P2: dead and deprecated-on-arrival public surface

- `FunctionNameInferenceTrigger` (`Affiant.Core.Triggers`) deleted. It carried
  `[Obsolete("... will be removed before v1.0.0")]` from the day it was written and had zero usage
  outside its own unit tests. Shipping a type marked deprecated in the first public package is
  strictly worse than shipping nothing. Use `WriteIntentInferenceTrigger` with `[AffiantWriteTool]`
  decoration; a host that genuinely needs allowlist triggering implements `IInferenceTrigger` itself.
- `AffiantCoreOptions.PrimaryProvider` and `.FallbackProvider` deleted. Nothing read either property
  anywhere, and their doc comment ("Passed to the SK kernel for automatic provider selection") was
  false: provider selection is governed by the separate `Affiant.Core.Services.SemanticKernelOptions`
  pair of the same names, configured through `AddAffiantSemanticKernel`. That class is unaffected.
- The five per-provider `IConnectorCapabilities` classes in
  `Affiant.SemanticKernel.Connectors.Capabilities` (`AzureOpenAiCapabilities`,
  `GoogleGeminiCapabilities`, `OllamaCapabilities`, `OpenAiCapabilities`,
  `OpenAiCompatibleCapabilities`) are now `internal`. Each was constructed exactly once, inside
  `CapabilityRegistry`; `CapabilityRegistry.Resolve(string)` returns the interface and never a
  concrete type, so no adopter code path could observe them. `CapabilityRegistry` itself stays
  public and DI-resolvable.

**Deliberately not removed:** `IDeterministicFieldSource` stays `[Obsolete]` in this release despite
the same policy conflict, because a live host still implements it (the Meridian reference app's
`AircraftLocationFieldSource`). Removing it now would mean rewriting a worked adopter example for
zero adopter-visible gain while the `[Obsolete]` attribute already warns at compile time. Its
removal, and the migration to `IFieldResolver` that must precede it, are scheduled for `1.0.0-beta.2`
and tracked as affiant#37.

### Added — Area-8 P2: type summaries on the primary adopter-facing Abstractions interfaces

`IDocketStore`, `IApprovalPolicy`, `IChatSessionStore`, `IWriteExecutor` and `IContextFabric` each
gained a type-level `<summary>`/`<remarks>` covering what the contract is for, whether an adopter
implements or merely consumes it, which package ships a ready implementation, and the trap where one
exists (`IDocketStore`'s rows-affected atomicity obligations, `IChatSessionStore`'s
append-vs-replace split, `IApprovalPolicy`'s null-means-defer chain semantics). These now reach
adopters' IntelliSense, since XML documentation generation was enabled in the same release.

### Fixed — Area-5 refuter round: double-broadcast race, SQLite migration drift, cancellation logging, D2 disclosure

Adversarial refuter pass over the Area-5 Docket-store-semantics wave (below), fixing four
CONFIRMED/HIGH findings:

- `DocketExpiryService`'s sweep no longer double-broadcasts `DocketExpired` for one entry when a
  concurrent `ReviewGate.HandleDecisionAsync` restart-path transition (affiant#14) independently
  wins the same status change between the sweep's snapshot and its own write. The sweep now
  CAS-transitions each entry individually and only broadcasts for the call whose own write
  affected the row.
- SQLite hosts with an already-provisioned database never picked up the new `ResubmittedTo`
  column (below) — `AffiantMigrator`'s SQLite branch used `EnsureCreatedAsync`, which no-ops
  against an existing database file regardless of schema drift. A narrow, idempotent
  `HealSqliteDriftAsync` step now adds the column and its index directly when a pre-existing
  Docket table is missing it.
- `ReviewGate.ResubmitAsync`'s orphan-pointer error log excluded `OperationCanceledException`, so
  a connection-tied cancellation during resubmission left only a generic warning with no
  correlated entry ids. Cancellation now logs at Error like any other exception on that path.
- Area-5 Decision 2's acceptance criterion 5 (surfacing "resubmitted" during reconciliation) was
  neither implemented nor disclosed as deferred. Now recorded explicitly, in `DocketEntry`'s and
  `ReviewStatusExtensions`' remarks, as an open, unruled, host-wave-scope decision.
- `IDocketStore.TryConsumeForResubmitAsync` renamed to `ConsumeForResubmitAsync` — a rows-affected
  `Task<int>` return under a `Try` prefix broke the `TryXxx` naming convention its own sibling
  `UpdateReviewStatusAsync` (the identical CAS idiom) already followed.
- **Known, recorded deviation, not fixed here:** `Affiant.Docket` hard-references
  `Affiant.EntityFramework` — an adapter-to-adapter layering violation that predates this branch.
  Fixing it requires moving which package owns the SQL store implementations; out of scope for a
  targeted refuter fix (tracked as affiant#35; see Area-8's packaging/layering rulings).

### Removed — Area-5 P1e/D1: `ReviewStatus.Amended` and `.Cancelled` deleted

Both enum members had zero writers anywhere in the framework or either host application.
`Amended` was superseded from day one by a different mechanism — approval carries edits via
`DocketEntry.Amendments` (the round-trip described in framework spec §2.7), never a status
transition — and its only mapping site already silently fell through to the `Expired` default,
disagreeing with both hosts' own `Amended → Approved` handling. `Cancelled` was unused scaffolding
from the original spec draft with no producer anywhere. Removing both makes every `ReviewStatus`
switch expression in the framework exhaustive under `TreatWarningsAsErrors`, so a future
reintroduction is compile-safe. Framework spec §2.7 and §9's code samples updated to match the
five surviving members: `Pending`, `Approved`, `Rejected`, `Expired`, `Deferred`.

### Changed — Area-5 P1d: `Status → Outcome` mapping becomes public framework API

`ReviewGate`'s internal status-to-outcome mapping was private, so both host applications had
already hand-copied it — and diverged from the framework's own fallthrough once already. It is
now `ReviewStatusExtensions.ToReviewOutcome()`, a public, exhaustive extension method on
`ReviewStatus` in `Affiant.Abstractions`, the layer that owns both `ReviewStatus` and
`ReviewOutcome`. Hosts should consume this instead of reimplementing the mapping. Non-terminal
statuses keep their prior semantics (`Pending → Expired`, `Deferred → Referral("deferred")`), now
documented via XML docs instead of an implicit discard arm.

### Fixed — Area-5 P1a: restart path persists `Expired` instead of computing-and-forgetting

`ReviewGate.HandleDecisionAsync`'s restart path (affiant#14) detected a lapsed review TTL by
timestamp and returned `ReviewOutcome.Expired` without persisting anything, leaving a
Pending-with-lapsed-TTL entry as the effective steady state for up to 30 seconds until the
background expiry sweep ran — and `ResubmitAsync` hard-requires `Status == Expired`, so it threw
for that whole window. The restart path now performs the same guarded compare-and-swap persist the
sweep uses and broadcasts `DocketExpired`, via a new shared `DocketExpiryBroadcaster` helper both
paths call — so the two can no longer drift on when it's safe to tell a session group an entry has
expired. Only the call whose own write affected the row broadcasts; a losing or repeat call
re-reads and reports the entry's actual terminal status (idempotent replay, no duplicate
broadcast).

### Fixed — Area-5 P1b: `FileDocketEntryAsync` honors the no-op contract on all three stores (affiant#32)

`InMemoryDocketStore.FileDocketEntryAsync` unconditionally overwrote an existing entry, so
re-filing an already-terminal `EntryId` silently reverted it to `Pending` with new data — no race
required, a straightforward contract violation. It now uses `TryAdd` under the store's existing
lock: first write wins, second call no-ops. The two SQL stores already did check-then-act, but the
loser of a genuine same-`EntryId` race got a raw, unhandled primary-key-violation exception instead
of the documented no-op; both now catch that exception and degrade to a no-op only once the row is
confirmed to already exist. A new cross-backend test files two different payloads under one
`EntryId` on all three stores and asserts the first payload survives.

### Added — Area-5 P1c/D2: `ResubmittedTo` lineage column, the affiant#31 race guard

Closes a double-resubmit race: two concurrent `ReviewGate.ResubmitAsync` calls against the same
expired entry could both mint a fresh `Pending` entry, since nothing marked the source entry
"consumed." `DocketEntry` gains a nullable, indexed `ResubmittedTo` column (new EF migration,
`AddResubmissionLineage`) that serves as both the race guard and the resubmission lineage —
`Status` stays `Expired`; there is no `ReviewStatus.Resubmitted`. A new
`IDocketStore.ConsumeForResubmitAsync` member (implemented on all three backends: an in-process
lock for InMemory, a guarded conditional update for SQLite/Postgres) returns the usual
rows-affected idiom so `ResubmitAsync` can tell a winning consume from a losing one and throw the
same "already processed or expired" error hosts already handle uniformly. A companion
`GetResubmissionParentAsync` reverse lookup re-derives a resubmitted entry's prior amendments on
reconnect, since they only ever traveled on the original (possibly-missed) broadcast. Framework
spec §2.7 gains a resubmission-and-lineage paragraph describing this. New concurrency tests
(`Task.WhenAll` across independent store instances, all three backends) prove exactly one of two
concurrent resubmit attempts succeeds.

### Added — Area-5 F/D3: at-least-once Evidence Card delivery by construction

Closes affiant#28 — a review filed while its target session's SignalR group has zero connected
members (a normal race: the reviewer's tab hasn't reconnected yet) previously stranded the entry:
the filing-time broadcast reaches nobody and nothing re-sends it. The background expiry sweep now
carries a third phase that unconditionally re-broadcasts the Evidence Card request for every entry
still `Pending`, regardless of whether the filing-time broadcast reported success — a SignalR group
send to zero members completes without error, so a delivery-tracking flag would only ever record
that the send happened, not that a human could see it; re-broadcasting by construction was chosen
over a flag for exactly that reason. `EvidenceCardRequestFactory` (`Affiant.Core.Services`) is now
the single place that builds this payload — the filing path, a new reconnect primitive
(`ReviewGate.RebroadcastPendingCardsAsync`), and the sweep all call it, so the three call sites
cannot independently drift. `ListPendingBySessionAsync` is now specified and pinned as
`CreatedAt`-ascending on all three backends.

### Added — Area-5 P2a/P2b: `AppendMessagesAsync` chat contract + framework `InMemoryChatSessionStore`

Closes the structural half of affiant#27 (the issue stays open — the production mitigation is a
host-side lock registry; see P3 below): `IChatSessionStore.AppendMessagesAsync(sessionId,
messages, ct)` is an append-only write that computes the next message ordinal and inserts under
one transaction, implemented on both SQL backends. It is deliberately distinct from
`SaveMessagesAsync`, which stays as the delete-and-reinsert rehydration write — affiant#27's actual
loss window was two concurrent full-replace calls each working from a stale snapshot, and
`AppendMessagesAsync` cannot degrade into that path. XML docs on both interface members now state
which write class each belongs to. `InMemoryChatSessionStore` ships in `Affiant.EntityFramework`
alongside its SQLite/Postgres siblings (mirroring where `InMemoryDocketStore` already lives
relative to its own SQL siblings) and is selectable via `AddAffiantEntityFramework`'s new
`UseInMemory()` provider option. Framework spec §3.2's `IChatSessionStore` sample updated to match
(it had drifted to a pre-refactor Semantic-Kernel-typed signature).

### Added — Area-5 P3: `SessionLockRegistry` promoted to `Affiant.Core`

Promotes the HR Portal host's `ConversationLockRegistry` (a per-session `SemaphoreSlim(1,1)`
turn-serialization lock) into the framework as `SessionLockRegistry`, DI-registered as a singleton
by `AddAffiantCore`. XML docs state its single-process caveat (no cross-process/instance
serialization) plainly. Ships unwired — investigation found neither shipped adapter has a
framework-owned seam to wire it into (the SK adapter's session rehydrator only reads store state;
the MAF adapter touches neither store interface at all); the load-mutate-save turn loop is
host-orchestrated end to end. The host wave adopts it directly, retiring HR Portal's own copy.

### Added — Area-5 P4: chat-store parity test suite, `SaveContext`/`LoadContext` coverage, genuine concurrency races

Closes test debt the store-parity investigation found escaping the framework entirely: a
`[Theory]`/`[ClassData]` parity suite now pins `IChatSessionStore` behavior (session and message
round-trips, `AppendMessagesAsync`, full-replace semantics, deletion) identically across InMemory,
SQLite, and PostgreSQL — previously the chat-store side had no cross-backend parity suite at all,
unlike the Docket store side. Also adds `SaveContextAsync`/`LoadContextAsync` round-trip coverage
and two genuinely concurrent (`Task.WhenAll`, independent store instances per side) races — double
status-update and double same-`EntryId` filing — across all three Docket backends.

### Added — Area-4 P4: `AffiantHub` is now `Hub<IAffiantHubClient>` — compile-time-checked client method calls

- **`AffiantHub` derives from `Hub<IAffiantHubClient>` instead of the untyped `Hub`.** A hub
  subclass's own `Clients.Caller`/`Clients.Group(...)`/etc. calls are now checked against
  `Affiant.Transport.SignalR.Hubs.IAffiantHubClient` at compile time — `Clients.Caller.ReceiveToken(chunk)`
  instead of the raw, typo-able `Clients.Caller.SendAsync("ReceiveToken", chunk)` string literal both
  reference hosts' hot-path token-streaming code used exclusively before this change (the strongest
  single piece of evidence in the whole area-4 investigation that the bypass was a shape gap, not
  host laziness — the framework's own hub base didn't route through its own enum either, until now).
- **`IAffiantHubClient`'s method names are locked to `TransportEventExtensions.ToClientEventName()`'s
  outputs** — one method per `TransportEvent` member, same names. `AffiantHubTypedClientTests`
  asserts the two sets are exactly equal (not a subset or superset either direction) via reflection,
  so the interface and the enum mapping can't silently drift apart.
- **Scope: C#-side compile-time safety only** — no TypeScript is generated or constrained.
  `IStreamingTransport` (used by `AffiantHub.Transport` and every framework service broadcasting
  from outside a hub context — `ReviewGate`, `ReviewGateFilter`, `DocketExpiryService`,
  `UiGuidanceBridge`) stays deliberately untyped: those callers have no `Clients` to type against,
  and two events (`AgentMessage`, `ContextUpdate`) genuinely have no dedicated payload record
  anywhere in the framework, so their typed-client parameter stays `object` rather than inventing a
  shape the framework doesn't otherwise define.
- **One structural conflict found and resolved, not worked around:** the SignalR integration test
  fixture's `TestAffiantHub` used to announce a client's own `Context.ConnectionId` via a test-only
  `"ConnectionRegistered"` push — not a real `TransportEvent` member, so it has no place on
  `IAffiantHubClient` and a typed `Hub<T>`'s `Clients` proxy structurally cannot carry it. Resolved
  by removing the workaround entirely: `Microsoft.AspNetCore.SignalR.Client.HubConnection.ConnectionId`
  is populated client-side after `StartAsync()`, so the server-side announcement was never necessary.
  No BLOCKED boundary remains for this item.
- Mutation-verified: renaming an `IAffiantHubClient` method reproduces a failure in
  `AffiantHubTypedClientTests`'s name-lock test; restored byte-identical.

### Changed — Area-4 P1g: framework spec §2.10 and §3.1 rewritten to match shipped code; Rule 6 corrected

- **Spec §2.10 "Event Vocabulary"** was written 2026-04-11, 19 days before the real `TransportEvent`
  enum was first implemented (2026-04-30), and was never reconciled afterward — it documented a
  10-member enum sharing only 2 names with the shipped one. Rewritten 2026-08-04 to list the real,
  post-this-wave 8-member enum and its real wire-name mapping (`EvidenceCardRequest` → `"ConfirmAction"`,
  `EvidenceCardResponse` → `"EvidenceCardResponse"` (document-reserved), `AgentMessage` →
  `"ReceiveToken"`, `ContextUpdate` → `"ContextUpdated"`, `SystemNotification` → `"SystemNotification"`,
  `DocketExpiring` → `"DocketExpiring"`, `DocketExpired` → `"DocketExpired"`, `UiGuidance` →
  `"GuideUI"`), with a historical note on the deleted `UserMessage` member.
- **Spec §3.1 "Transport"** documented only `IStreamingTransport`'s original three methods and was
  never updated for the same-day additions of the blocking-await method and its decision-delivery
  counterpart. Rewritten to the real, post-this-wave interface, with the blocking
  `AwaitEvidenceCardResponseAsync`'s document-reserved status and deadlock history stated plainly
  (host-apps#25, redesign tracked as `affiant#29`), and an explanation of what was deleted
  (`TransportMessage`, `ReceiveAsync`) and why.
- **Rule 6's spec text corrected** to describe the now-real mechanism (P1f(b)): `UiGuidanceBridge`
  now carries the wire path itself instead of the rule describing a discovery contract with no
  framework-owned delivery mechanism behind it.
- `CLAUDE.md`'s Rule 6 summary was checked and left unchanged — it describes only the discovery
  contract (`IRouteRegistry`, never DOM inspection), which was already accurate and remains so; it
  never claimed a specific wire-delivery mechanism, so it had nothing to correct.

### Added — Area-4 P1f(b): Rule 6 becomes real — framework-owned UI guidance wire path (`TransportEvent.UiGuidance`, `UiGuidanceBridge`)

- **Rule 6 ("the LLM discovers guidable elements through `IRouteRegistry`, never DOM inspection")
  previously had no framework wire path at all** — `IRouteRegistry` existed, but nothing broadcast
  its contents to a client; the one reference host's own guidance feature reached the wire via a
  hand-rolled `Clients.Group(...).SendAsync("GuideUI", ...)` call, entirely outside the framework's
  transport abstraction. Ruled BUILD NOW (2026-08-04, against the position paper's docs-first
  recommendation): the framework now carries this designed flow instead of leaving hosts to invent
  it, consistent with the P5a/P5c precedent set earlier in this wave.
- **`TransportEvent.UiGuidance`** added, mapped to wire method name **`"GuideUI"`** — the existing
  reference host's client already listens for that exact name; the mapping preserves it deliberately
  so that client keeps working unmodified once it migrates onto this mechanism.
- **`Affiant.Abstractions.Transport.UiGuidancePayload`/`UiGuidanceStep`** — a typed payload pinned
  from the reference host's own existing wire contract (its checked-in guidance tool output shape
  and its client-side TypeScript interfaces, both read at framework main `fc46b95`), not invented:
  `UiGuidancePayload(NavigateTo, Steps, Context)`; `UiGuidanceStep(ElementId, Title, Description,
  PrefillValue, Side, HighlightPadding)`.
- **`UiGuidanceBridge` (`Affiant.Core`) now assembles and broadcasts**, not just reads:
  `BuildStep(elementId, description, prefillValue?, title?)` resolves `Side`/`HighlightPadding`/a
  `Title` fallback from the `GuidableElement.Attributes` registered for `elementId` (the same
  `"side"`/`"highlightPadding"`/`"displayName"` attribute-bag convention the reference host used ad
  hoc — degrades to `"bottom"`/no-padding-asserted/`elementId` when unregistered, never throws) and
  `BroadcastGuidanceAsync(sessionGroupId, payload, ct)` sends it via `IStreamingTransport`
  (`TransportEvent.UiGuidance`). `Affiant.Core` depends only on `IStreamingTransport` (an
  `Affiant.Abstractions` interface) — never the `Affiant.Transport.SignalR` package, preserving the
  DAG layering invariant. **What stays host-owned**: per-step content (which fields to guide
  through, prefill values, description text by provenance) — inherently domain-specific, mirroring
  `ReviewGate`'s own framework-mechanism/host-content split.
- `UiGuidanceBridge` now also gets a real `AddAffiantCore()` registration (Singleton — neither
  constructor dependency is Scoped, so no captive-dependency risk), matching affiant#26's fix for
  `ReviewGate` — a host no longer hand-registers it.
- Tests: `UiGuidanceBridgeTests` (unit — attribute resolution, defaults, exact broadcast call);
  `UiGuidanceBridgeWireTests` (real SignalR round trip proving `"GuideUI"` delivery with the pinned
  JSON shape). Mutation-verified: routing the broadcast through the wrong `TransportEvent`
  reproduces failures in both; restored byte-identical.

### Changed — Area-4 P1d: hub JSON serialization policy is now DECLARED, not inherited from ambient ASP.NET defaults — **`ApprovalDecision` now crosses the wire as a STRING, not an int**

**Wire-visible break for any host that has already deserialized a raw `EvidenceCardResponse` off the
wire expecting `decision` to be a JSON number.** `AddAffiantSignalR` now calls `.AddJsonProtocol(...)`
explicitly, configuring:

- **camelCase property naming** — unchanged in practice (this was already ASP.NET Core's
  `JsonHubProtocol` default, `JsonSerializerDefaults.Web`), but now declared in framework source
  instead of asserted only in a test comment. A host that later configures `.AddJsonProtocol(...)`
  after `AddAffiantSignalR` can still override it — TryAdd semantics don't apply to protocol
  configuration, so call order matters; configure yours after `AddAffiantSignalR` to win.
- **A global `JsonStringEnumConverter`** — this is the wire-visible change.
  `Affiant.Abstractions.Transport.ApprovalDecision` (the `EvidenceCardResponse.Decision` field) had
  no `[JsonConverter]` attribute and previously crossed the wire as a bare integer (`0` = Approved,
  `1` = Rejected) via `System.Text.Json`'s default enum handling — inconsistent with
  `Affiant.Abstractions.Models.ProvenanceSource`, which is explicitly
  `[JsonConverter(typeof(JsonStringEnumConverter))]`-attributed and always crossed as a string. Both
  now cross as strings (`"Approved"`/`"Rejected"`), resolving the inconsistency V1 flagged. Any host
  TypeScript that typed `decision` as `number` or compared it against `0`/`1` needs to change to the
  string values on the next pin bump. `AffidavitFieldKind` is unaffected — it was already a plain
  string constant, never a C# enum, by prior deliberate design.
- The framework contract test now asserts the policy **from the configured `IOptions<JsonHubProtocolOptions>`**
  (`SignalRJsonProtocolConfigurationTests`), not by observing a live round trip work and inferring
  the policy from that (which cannot distinguish "we configured this" from "it happens to work via
  an unrelated ambient default" — precisely the gap that let the inconsistency ship unnoticed).
  A live-wire proof (`ApprovalDecision_CrossesTheWire_AsAString_NotAnInt`) additionally confirms the
  actual bytes on the wire, not just the configured options object.
- Mutation-verified: removing the `.AddJsonProtocol(...)` call reproduces failures in the
  enum-converter and live-wire tests (the camelCase assertion still incidentally passes, since it
  remains ASP.NET Core's ambient default — exactly the "accidental but real" behavior this change
  makes deliberate instead); restored byte-identical.

### Changed — Area-4 P1c: `TransportEventExtensions.ToClientEventName()` is now `public` and total

- **Every `TransportEvent` member now gets an explicit switch arm** — no `default`/discard
  fallthrough to `evt.ToString()`. Before this change, 4 of 8 members (now 3 of 7, after P1a's
  `UserMessage` deletion) silently fell through to `.ToString()`, meaning a rename or reorder of
  those members would silently rename the SignalR wire method with zero compiler signal — exactly
  the drift class this review area exists to eliminate. Adding a `TransportEvent` member without a
  matching arm is now a build failure (CS8509, turned into an error by this package's
  `TreatWarningsAsErrors`).
- **`TransportEvent` renumbered contiguously** (`EvidenceCardRequest=0` … `DocketExpired=6`, no gap
  where `UserMessage` used to sit at 3) — required for the switch to be provably exhaustive over
  every *named* member without a discard arm; the enum's integer values are never serialized over
  the wire (only the mapped string method name is), so this carries no wire risk.
- **`ToClientEventName()` is now `public`** (was `internal`) — a host's own contract net can call it
  directly instead of reaching it only through reflection, which detected removal but not an
  output-string change.
- A `#pragma warning disable CS8524` brackets the switch — a distinct, unavoidable diagnostic every
  enum switch *expression* without a discard arm triggers in C# (enums admit any underlying integral
  value via casting, not just named members). Suppressing it does not weaken the guarantee: CS8509
  (a genuinely missing *named* member) remains fully active.
- `TransportEventExtensionsExhaustivenessTests` pins the exact current wire-name mapping for every
  member directly (no reflection) and asserts every named `TransportEvent` value has a pinned
  expectation. Mutation-verified twice: (1) adding an enum member without a matching arm reproduces
  CS8509 at build time; (2) silently changing an existing arm's output string reproduces test
  failures in both the new exhaustiveness tests and the existing round-trip contract tests; both
  restored byte-identical.

### Added — Area-4 P1b: `SystemNotificationPayload` — named record replacing the duplicated anonymous `{level, message}` object

- New `Affiant.Abstractions.Transport.SystemNotificationPayload(string Level, string Message)`.
  Both framework call sites that broadcast `TransportEvent.SystemNotification` — `ReviewGate`'s
  best-effort broadcast-failure notification and `ReviewGateFilter`'s P1a filing-failure
  notification — previously each defined their own identical anonymous `{ level, message }` object;
  both now construct the same named record.
- **Wire shape is unchanged on purpose.** Still camelCase `{level, message}`, still exactly those
  two properties — existing host TypeScript keeps working across the pin bump with no client-side
  change. Locked by `SystemNotificationPayload_SerializesToUnchangedWireShape` (real SignalR round
  trip asserting the JSON has exactly `["level", "message"]` as its property set).
- `Level` stays a plain `string`, not a C# enum — its allowed values (`"error"`/`"warning"`/`"info"`)
  are meant to be pinned by the host-apps contract net's closed-set value fixtures (Area-2 P2d), not
  by a framework-side type, deliberately avoiding the int-vs-string enum-serialization inconsistency
  V1 documented between `ApprovalDecision` (int) and `ProvenanceSource` (string).
- Mutation-verified: reverting either call site back to its anonymous object reproduces the
  corresponding typed-payload assertion failure in `ReviewGateFilterTests`/`ReviewGateTests`;
  restored byte-identical.

### Removed — Area-4 P1a/P1e: dead transport vocabulary deleted; blocking review path retired as document-reserved

- **`TransportEvent.UserMessage` deleted.** Founding-commit symmetry filler paired with
  `AgentMessage` in the same commit — never specified anywhere in the framework spec, never
  emitted in production by the framework or either reference host (confirmed by the area-4
  Decision-1 archaeology: `docs-area-4-d1-fw-intent.md` / `d1-host-bypass.md`, finding A). Inbound
  chat text enters the framework as a SignalR hub RPC parameter (a host-defined
  `SendMessage(message, conversationId)` method — SignalR's own client→server invoke pattern), not
  a broadcast `TransportEvent`. Its round-trip contract test (`SignalRTransportContractTests.RoundTrip_UserMessage`)
  is deleted with it — it validated a capability the framework never used.
- **`TransportMessage` and `IStreamingTransport.ReceiveAsync` deleted**, along with the SignalR
  implementation's `NotSupportedException` stub. Explicit second-transport scaffolding (the type's
  own doc comment named "SignalR, WebSocket, etc.") for a second, pull-based transport that was
  never built, never discussed anywhere in the design record, and — on the one transport that does
  exist — was a dead-on-arrival `NotSupportedException`. Textbook speculative abstraction per this
  repo's own `CLAUDE.md` ban.
- **Blocking review path retired as document-reserved, not deleted.** `ReviewGate.FileReviewAsync`
  (and the interface members it depends on) remain callable — kept because the underlying design
  (a synchronous wait-for-external-event, mirroring the Azure Durable Functions
  `WaitForExternalEvent` pattern; framework spec §4) is legitimate and has a real future use — but
  their XML docs now state plainly, with the incident evidence inline, that this path structurally
  deadlocks over the framework's only shipped transport (SignalR, default
  `MaximumParallelInvocationsPerClient = 1`, host-apps#25) and is not the production default (P5a's
  `ReviewGateFilter` calling the non-blocking `FileForReviewAsync` is). A sound redesign — the
  decision traveling on a channel other than the blocked connection — is tracked in affiant#29
  (design ticket, no implementation planned yet).
- **`IStreamingTransport.AwaitEventAsync<T>` de-genericized to `AwaitEvidenceCardResponseAsync`.**
  The generic type parameter only ever legally bound `EvidenceCardResponse` — every implementation
  (the sole SignalR one) runtime-threw `NotSupportedException` for any other `T`, a compile-time
  promise the framework never had a second use case to honor, and exactly the "orphaned reference
  doesn't surface until you hit it" failure class this review area exists to eliminate. The
  signature now states the one real contract directly; a caller can no longer even attempt
  `AwaitEventAsync<SomeOtherType>()` and discover the lie at runtime.
- **Breaking, pre-1.0 clean break.** Every affected test's fakes/stubs updated to the new
  `IStreamingTransport` shape across `Affiant.Core.Tests`, `Affiant.SemanticKernel.Tests`,
  `Affiant.AgentFramework.Tests`, `Affiant.Docket.Tests`, and `Affiant.Transport.SignalR.Tests`.

### Changed — Area-4 P5a: `ReviewGateFilter` is now non-blocking by default — the framework's own filing filter

- **`ReviewGateFilter` (the neutral, both-adapters completion-stage filter) now calls the
  non-blocking `ReviewGate.FileForReviewAsync` instead of the blocking `ReviewGate.FileReviewAsync`.**
  The blocking predecessor awaits a reviewer decision inline and structurally deadlocks over
  single-connection SignalR (host-apps#25: `MaximumParallelInvocationsPerClient = 1` starves the
  very `ApproveAction`/`RejectAction` invocation that would deliver the decision the blocked
  `SendMessage` invocation is awaiting — "live approval has plausibly never once succeeded through
  the browser UI"). Both reference hosts independently hand-built the non-blocking pattern this
  filter now ships as the framework default (Meridian's `ChatHub`; HR Portal's bespoke
  `HRPortalReviewFilingFilter`, which HR Portal had to write from scratch and re-derive the P1(a)
  protocol for, purely because the framework's own filter was unusable at the time).
- On `ReviewFilingResult.RequiresReview` (a human reviewer must act — the Evidence Card is already
  broadcast by `FileForReviewAsync` itself), the filter now sets `Terminate = true` and ends the
  turn with a model-facing message. On `ReviewFilingResult.Decided` (already resolved without a
  client round trip — StandingOrder auto-approval, Referral escalation, or idempotent replay), the
  filter does **not** terminate — the tool's own original result is left untouched and the model
  continues normally.
- **Runs identically on both adapters with zero adapter-specific code** — `ReviewGateFilter` is a
  neutral `ICompletionStageFilter`, registered once by the shared
  `AddAffiantCompletionFilters()` helper that both `AddAffiantSkFilters()` (SK) and
  `AddAffiantAgentFramework()` (MAF) call. `ReviewGateFilterMafBoundaryTests` pins this — the MAF
  seam gets the identical non-blocking, terminating behavior through only the public
  `AddAffiantCore()` + `AddAffiantAgentFramework()` chain, no MAF-specific filing filter needed.
- **Registration order was never the problem for this filter, and item 1's affiant#25 fix makes
  that provable, not assumed.** Unlike a host's own `IAutoFunctionInvocationFilter`, this filter
  runs *inside* `AffiantAutoFunctionInvocationBridge`'s own neutral pipeline — its `Terminate`
  decision is baked into the bridge's result before the bridge's own final assignment runs, so it
  never competed for position in SK's filter list and never needed HR Portal's
  `kernel.AutoFunctionInvocationFilters.Insert(0, ...)` workaround.
  `ReviewGateFilterOrderingTests` proves this empirically: the real bridge, wired through the
  standard *appended* `AddAffiantSemanticKernel()` DI chain with no special registration handling,
  still ends the turn on `RequiresReview`.
- **P1a filing-failure protocol (affiant#22) carried over intact** — typed `ReviewFilingFailed`
  `ToolError` sealed first, `affiant.review.filing_failed` OTel event, best-effort guarded
  `SystemNotification` broadcast, `OperationCanceledException` still propagates unchanged. Only the
  filing call itself changed (`FileReviewAsync` → `FileForReviewAsync`); the failure path's ordering
  and wording are unchanged.
- Mutation-verified: disabling the `Terminate`/turn-ending-message branch reproduces failures in
  `ReviewGateFilterTests`, `ReviewGateFilterOrderingTests` (SK), and `ReviewGateFilterMafBoundaryTests`
  (MAF) simultaneously — proving the shared-filter design actually shares, not just compiles;
  restored byte-identical.
- **MAF boundary, documented not fixed (out of framework scope):** `Affiant.AgentFramework` places
  no constraint on what a host's `AIFunction` body returns — same as SK. Meridian's specific write
  tools (host code, read-only reference) emit plain JSON with no `$type` discriminator, deviating
  from the documented tool-authoring contract (`docs/tool-authoring-guide.md`: "Always serialize the
  return value with `.ToJsonString()`"), so `ReviewGateFilter`'s existing envelope-shape catch clause
  silently skips them exactly as it did before this change — a host non-conformance issue, not
  something this wave's rewrite could or should paper over from the framework side.
  `ReviewGateFilterMafBoundaryTests.NonConformingPayload_...` pins this exact failure mode as a fact.

### Changed — Area-4 P5c: `AffiantHub`'s own broadcast helpers now route through `IStreamingTransport`, typed

- **`AffiantHub.BroadcastToSessionAsync`/`BroadcastToReviewerAsync` took a raw `string method` and
  called `Clients.Group(...).SendAsync(...)` directly** — the framework's own hub base bypassing its
  own `TransportEvent`/`ToClientEventName()` abstraction, the single strongest piece of evidence in
  the area-4 investigation that hosts' identical bypass (raw `Clients.Group(...).SendAsync("ReceiveToken", ...)`
  literals) was a shape gap, not laziness. Both helpers now take a typed `TransportEvent` and a
  required (non-null) `payload`, delegating to a newly-injected `IStreamingTransport Transport`
  property on the hub base — the same `BroadcastToGroupAsync(groupId, ...)` path both hosts
  independently converged on for their own session broadcasts, now first-class on the framework's
  hub instead of hand-rolled per host. **Breaking**: `AffiantHub`'s constructor now requires an
  `IStreamingTransport` (already registered by `AddAffiantSignalR`, so no new host wiring — only
  hub subclasses that call `base(chatSessionStore)` directly must add the second argument), and the
  two broadcast helpers' second parameter changed from `string method` to `TransportEvent eventType`
  with `payload` no longer optional. Locked by `AffiantHubBroadcastHelperTests` (unit-level: a spy
  transport proves the correct group name and `TransportEvent` reach `IStreamingTransport`;
  integration-level: two real-SignalR round trips prove wire delivery through `ToClientEventName()`'s
  mapping, including that the reviewer helper targets `reviewer:{id}`, not the bare id).
  Mutation-verified: reverting the session helper to the old raw-`Clients.Group` bypass reproduces
  both the unit and the integration test's failure; restored byte-identical.

### Fixed — Area-4 P5 prerequisites: completion-stage Terminate preserved across bridges; ReviewGate gets a real DI registration (affiant#25, affiant#26)

- **affiant#25 — `AffiantAutoFunctionInvocationBridge` (SK) and `AffiantFunctionInvocationMiddleware`
  (MAF) no longer overwrite a downstream filter's decision to end the turn.** Both bridges
  unconditionally assigned `context.Terminate = resultContext.Terminate` as their last line,
  discarding any `Terminate = true` a completion filter registered *after* the bridge (the normal,
  appended DI position) had set on the native context during its own `next()` call — the neutral
  pipeline's own Terminate verdict silently won every time, even when it had no opinion. This forced
  the host-apps `HRPortalReviewFilingFilter` workaround of inserting itself at
  `kernel.AutoFunctionInvocationFilters[0]` (running *before* the bridge) just to make its own
  termination signal survive. Fixed by capturing the native context's `Terminate` immediately after
  `next()` returns and OR-ing it with the neutral pipeline's verdict, on both adapters. Locked by
  `DownstreamTerminatePreservationTests` (drives both real bridges with a fake downstream
  continuation that sets `Terminate = true`); mutation-verified (restoring either unconditional
  overwrite reproduces the failure on the corresponding test).
- **affiant#26 — `ReviewGate` now has a real registration in `AddAffiantCore()`.** Previously every
  host had to hand-register `ReviewGate` itself, with no compile- or boot-time signal when they
  forgot — `ReviewGateFilter` resolves it via `context.Services.GetService<ReviewGate>()` (not
  `GetRequiredService`) and silently no-ops when absent, so a missed registration produced
  silently-unfiled write proposals rather than an error. Registered **Scoped** — it constructor-
  injects `IApprovalPolicyEvaluator` (Scoped since affiant#19), so a Singleton `ReviewGate` would be
  a captive dependency the moment a host policy carries a Scoped dependency (e.g. a `DbContext`).
  Locked by a boot-honesty test (`AddAffiantCore_AddAffiantSemanticKernel_Alone_WireReviewGate_FilingFilterRuns`)
  that builds a provider from only the public `Add*` extensions plus the genuinely host-owned
  interfaces (`IStreamingTransport`, `IDocketStore`, `IReviewContextProvider` — none have a default
  framework implementation) and proves the real filing filter runs end to end, under
  `ValidateScopes`/`ValidateOnBuild`.

### Fixed — one failure contract across both adapters; filter order matches spec; extraction failures never touch a genuine tool result (area-3 P2)

- **Ruling 1 — SK's completion stage is no longer a hole in the failure contract.** Previously, on
  SK, `TaskInferenceMergeFilter`/`ReviewGateFilter` ran in a *second, separate*
  `ToolInvocationPipeline.RunAsync` call with `ToolErrorFilter` structurally absent — an exception
  surviving either filter propagated raw into SK's own auto-invocation loop, able to fault the
  entire chat turn (not just one tool call), unlike MAF's single onion where `ToolErrorFilter`
  already wrapped everything (area-3 V4). `Affiant.SemanticKernel.Filters.BridgeStages.CompletionStage`
  now also includes `ToolErrorFilter`. Completion-stage filters are identified structurally via a
  new `Affiant.Abstractions.Interfaces.ICompletionStageFilter` marker interface
  (`TaskInferenceMergeFilter`/`ReviewGateFilter` implement it) instead of a closed type list, so a
  future third completion-stage filter inherits the guarantee automatically. Cross-adapter parity
  proven by injecting the same failure through both adapters' real bridges
  (`CrossAdapterCompletionStageFailureContractTests`) and asserting the model-visible payload is
  identical on both. **Shipped with the retry-safety flag gate from the start of this Unreleased
  entry** — a fix-round adversarial review caught, on this same unreleased branch (never merged,
  never published), that gating the completion-stage retry on `ToolExecuted` alone was insufficient:
  SK's completion-stage `next()` is SK's own auto-invocation continuation, not the tool, so a
  pre-tool-style failure there (`ToolExecuted` still false) could retry into a genuine second tool
  execution. New `ToolInvocationContext.NextIsToolBody` (default `true`) closes this: SK's
  completion-stage bridge sets it `false`, and `ToolErrorFilter`'s retry branch checks
  `!ToolExecuted && NextIsToolBody`. MAF's single onion is unaffected (`next()` genuinely is the
  tool there). Mutation-locked by `CompletionSeamRetrySafetyTests` (SK: `next()` called exactly
  once, no retry; MAF control: `next()` called exactly twice, retry still fires — the asymmetry is
  now deliberate and tested). See framework spec §3.12.9 "Retry safety at the completion seam."
- **Ruling 2 — filter order: code now matches the spec.** Framework spec §3.12.4 and
  `ToolErrorFilter`'s own class doc both claimed `ToolErrorFilter` was outermost; `AddAffiantCore()`
  actually registered `DeterministicShortCircuit` first, making it the true outermost filter — a
  documented-but-unenforced order the pipeline-order test never actually checked (area-3 V4).
  `AddAffiantCore()` now registers `ToolErrorFilter` first.
  `AffiantFilterPipelineOrderTests.NeutralFilters_RegisteredInCanonicalOrder` now locks the FULL
  7-filter chain (including `ToolTracingFilter`, previously untested) as one ordered sequence
  instead of a subset of pairs; verified by self-mutation (swapping the two registrations back
  reproduces the original bug and fails the test with a precise message naming both filters).
  Consequence, handled deliberately: a bug in a host's `IIntentInterceptor`
  (`DeterministicShortCircuit`'s dependency) is now also caught and converted to a typed
  `ToolError` instead of propagating raw out of the neutral pipeline — previously it bypassed
  `ToolErrorFilter` entirely.
- **Ruling 3 — retry is scoped to the tool body; post-processing failures surface-and-continue,
  never discard a result, never re-execute the tool (gate ruling: extraction policy =
  surface-and-continue).** Previously, `ToolErrorFilter`'s retry-once wrapped the entire remaining
  onion: a bug in a host `ContextExtractor` subclass or `TaskInferenceMergeFilter` could discard a
  genuinely successful tool result and report failure to the model, or — if classified retryable —
  execute the real tool a second time for a failure that had nothing to do with the tool
  (area-3 V5). New `Affiant.Abstractions.Models.ToolInvocationContext.ToolExecuted` flag, set by
  every bridge/middleware's terminal delegate the instant the real tool call succeeds, governs
  `ToolErrorFilter`'s catch decision: `ToolExecuted == false` is a genuine tool-body failure
  (existing map-and-retry-once behavior, unchanged); `ToolExecuted == true` is a post-processing
  failure — `ToolErrorFilter` now never touches `Result` and never retries in that case, only logs
  + emits an `affiant.extractor.failed` OTel event. `ContextExtractor`'s base class and
  `TaskInferenceMergeFilter` additionally self-guard their own post-tool logic with the same
  pattern (belt-and-suspenders with the `ToolErrorFilter` backstop above); `ReviewGateFilter`
  already self-guarded as of P1a. Applies identically to both backends — the whole mechanism lives
  in the neutral `Affiant.Core`/`Affiant.Abstractions` layer. Mutation-locked by
  `ToolBodyVsPostProcessingTests` (counting fake tool + throwing extractor: tool runs once, model
  sees the genuine result, OTel event fires; retryable tool failure: tool runs twice, extractor
  runs exactly once, on the final result). See spec §3.12.9 (new) for the full design writeup.
- **Ruling 4 (partial, framework side) — `ToolError.Code` registry.** New
  `Affiant.Abstractions.Models.ToolErrorCodes` declares every code the framework itself emits
  (`DB_TIMEOUT`, `UPSTREAM_UNAVAILABLE`, `VALIDATION_FAILED`, `UNKNOWN`, `REVIEW_FILING_FAILED`,
  `FUNCTION_NOT_FOUND` — enumerated from the code per area-3 V6, not the position paper's estimate
  of "4"). `ToolErrorFilter`/`ReviewGateFilter` now consume these constants.
  `Affiant.Testing.ComplianceHarness.ComplianceHarness.AssertToolErrorCodeRegistryParity` gives
  hosts the same opt-in, additive drift-detection `AssertToolNameRegistryParity`/
  `AssertFabricKeyParity` already provide, for their own domain codes. Host-side adoption of a
  host's own ~10 domain codes remains deferred to the area-3 closing wave — nothing here breaks a
  host that has not adopted it.
- **Ruling 4 (fix round, framework side complete) — scoping correction + a live emission lock.**
  `ManualToolInvoker`'s hand-written `FUNCTION_NOT_FOUND` JSON literal was wrongly grouped with
  host-side adoption above and deferred — it is a FRAMEWORK code; that scoping error is corrected
  here. `ManualToolInvoker.CaptureAndInvokeAsync` now builds its not-found payload through the real
  `ToolError` type consuming `ToolErrorCodes.FunctionNotFound`. The mismatched bare-`"TIMEOUT"`/
  `"DB_CONN_TIMEOUT"` assertions in `ToolEnvelopePolymorphismTests` now assert
  `ToolErrorCodes.DbTimeout`. Separately: an adversarial refuter proved by mutation that
  `AssertToolErrorCodeRegistryParityTests.FrameworkRegistry_MatchesEveryCodeTheFrameworkActuallyEmits`
  has zero power to catch a NEW bare-literal emission site — its "emitted" list is hand-typed from
  the same `ToolErrorCodes` constants it checks against, so it can only ever catch an orphan. A
  rogue `"RATE_LIMITED"` classification arm added to `ToolErrorFilter.MapExceptionToToolError`
  failed nothing (306 relevant tests green). New
  `Affiant.Testing.ComplianceHarness.Tests.AssertToolErrorCodeSourceScanTests` closes the gap: reads
  `src/` from disk and fails on any bare literal in the three shapes a `ToolError`-code emission
  site takes in this codebase (`Code: "LITERAL"`, a `(code, retryable)` classification-tuple arm, or
  hand-rolled JSON `"code":"LITERAL"`) — proven to catch both the rogue-arm mutation and a reverted
  `ManualToolInvoker` literal, restored byte-identical after each proof. The parity assertion is
  kept (it still legitimately catches orphans the source scan cannot); its own remarks now document
  the division of labor.
- **Docs (P4 rider).** Framework spec §6 gains the Area 3 gating principle verbatim from the
  position paper (`docs/architecture-review/area-3-tool-calling-reliability.md`, external
  `affiant-chancery` review repo), with a glossary and an honest field-evidence note that ToolMode
  forcing is entirely host-side (not present in this repo, so this document makes no claims about
  its host-side test/trace evidence). §3.12.4 corrected to the now-enforced order and now cites the
  full-chain lock test; new §3.12.9 documents the tool-body/post-processing policy. §2.4 documents
  the new `ToolErrorCodes` registry. `docs/tool-authoring-guide.md` (v1.3-alpha): Section 2's
  primary DbContext example inverted per affiant#21 — per-invocation scope-factory resolution is
  now the default pattern for any `Scoped` plugin dependency, with direct constructor injection
  called out as the anti-pattern (previously the reverse); the quick-reference minimal read/write
  tools in §8 corrected to match; §6 flags (does not fix) the pre-existing `Retryable: true`
  doc/code mismatch for directly-returned `ToolError`s (area-3 V6).
- **Docs (fix round).** §3.12.9 gains "Retry safety at the completion seam," correcting the
  disproven "structurally impossible"/"cannot double-fire" claims that shipped in the P2 landing
  above with the `NextIsToolBody` mechanism that actually governs it; §3.12.4 cross-references it.
  §2.4 records the `FUNCTION_NOT_FOUND` scoping correction and the new source-scan lock. The same
  disproven claims in `ToolErrorFilter.cs`'s and `BridgeStages.cs`'s own XML remarks are corrected
  in the source, not just the spec.

### Fixed — ReviewGateFilter no longer silently loses a WriteProposal (affiant#22, area-3 P1a/P1d)

- **Filing failure now surfaces to the model, the client, and the operator.** Previously,
  `ReviewGateFilter` caught every non-cancellation exception from `ReviewGate.FileReviewAsync` and
  only wrote a `LogError` — if the docket store threw before persisting, the proposal was gone with
  no docket entry, no retry, and no signal anywhere, even though the model had already told the user
  the action was filed for review. `ReviewGateFilter` now:
  - Rewrites the tool result to a typed `ToolError` (new code `ToolErrorFilter.ReviewFilingFailedCode`
    = `REVIEW_FILING_FAILED`) whose message states the proposal was **not** filed and **not** queued
    for review.
  - Best-effort broadcasts a `SystemNotification` on the same session-group transport channel used
    for Evidence Cards (both hosts render `SystemNotification` since Area-2 P1) — guarded so a
    broadcast failure here can never mask the `ToolError` already sealed onto the result.
  - Emits an `affiant.review.filing_failed` OTel event (tagged `tool_error.code` +
    `exception.type`) on the nearest ambient `Affiant.Framework` activity.
  - `OperationCanceledException` still propagates unchanged — cancellation is not a filing failure.
  - **Verified on both adapters:** MAF runs every neutral filter in one onion, so the rewritten
    result is what `AffiantFunctionInvocationMiddleware` returns directly. SK runs
    `ReviewGateFilter` in a separate completion-stage `pipeline.RunAsync` call with `ToolErrorFilter`
    structurally absent from that seam (V4) — but because `ReviewGateFilter` now catches its own
    filing failure and returns normally, nothing needs to escape that call for the rewrite to reach
    `AutoFunctionInvocationContext.Result`, confirmed by a direct-construction test against the real
    `AffiantAutoFunctionInvocationBridge`.
- **Evidence Card broadcast failure after a successful filing no longer orphans the entry silently.**
  `ReviewGate` now retries the Evidence Card broadcast once; if both attempts fail, the DocketEntry
  (already durably `Pending`) is left as-is, an `affiant.review.broadcast_failed` OTel event fires,
  and a best-effort `SystemNotification` goes out. Filing still reports success — the proposal
  genuinely is filed and discoverable via `ListPendingBySessionAsync`, and reporting failure would
  invite a caller to re-file, creating a duplicate docket entry.
  - **Documented residual risk:** `DocketEntry` has no field marking whether the broadcast ever
    succeeded — adding one would require an `IDocketStore` schema migration (a new column shared by
    `SqliteDocketStore`/`PostgresDocketStore`'s EF entity), which is out of scope for this change.
    The only durable signal today is the log line and the OTel event; the entry itself is
    indistinguishable in the store from one whose broadcast succeeded on the first try. Area 5 (store
    reconciliation) owns closing this gap. See `DocketEntry`'s class remarks and
    `ReviewGate.BroadcastEvidenceCardWithRetryAsync`'s remarks.
- **Telemetry honesty for RETURNED `ToolError`s (area-3 V6/P1d).** `ToolTracingFilter` previously
  tagged every non-null tool result `tool_status="ok"`, even when the result was itself a `ToolError`
  envelope (e.g. a host's redirect protocol, or `ReviewGateFilter`'s own filing-failure rewrite on
  MAF's onion) — invisible to Jaeger. It now detects the `ToolEnvelope` `$type:"error"` discriminator
  on the post-invocation result and, when found, tags `tool_status="error"` and emits the same
  `affiant.tool_error` event shape `ToolErrorFilter` emits for thrown errors (`tool_error.code`,
  `tool_error.retryable`, `exception.type`) — one operator-visible vocabulary for both thrown and
  returned tool failures. Distinguished from a thrown error via the new sentinel
  `ToolTracingFilter.ReturnedToolErrorExceptionType` ("ReturnedToolError") in the `exception.type` tag,
  since no CLR exception exists to name for a returned envelope.
- `AffiantTelemetry.FindAffiantActivity()` extracted from `ToolErrorFilter` (was a private method) so
  all three emitters (`ToolErrorFilter`, `ReviewGateFilter`, `ReviewGate`) share the same "walk up to
  the nearest `Affiant.Framework` span" logic instead of duplicating it.
  - Mutation-locked: `ReviewGateFilterTests`, `AffiantFunctionInvocationMiddlewareTests` (MAF), and
    `AffiantAutoFunctionInvocationBridgeReviewGateTests` (SK, new) all fail when `ReviewGateFilter`'s
    catch block is reverted to log-only.

### Fixed — ApprovalPolicyEvaluator captive dependency (affiant#19)

- **`ApprovalPolicyEvaluator` / `IApprovalPolicyEvaluator` are now registered `Scoped`** by
  `AddAffiantCore()` (was `Singleton`). The evaluator constructor-injects
  `IEnumerable<IApprovalPolicy>`, and `Affiant.Policies`' `AddStandingOrder<TPolicy>()` /
  `AddReferralRule<TRule>()` register policies `Scoped` by default — so a Singleton evaluator was a
  captive dependency the moment any policy had a scoped dependency (a host `DbContext` being the
  common case). Two symptoms, same root cause: hosts with `ValidateScopes`/`ValidateOnBuild` on (the
  ASP.NET Core Development default) crashed at boot with "Cannot consume scoped service ... from
  singleton 'ApprovalPolicyEvaluator'"; hosts without validation booted fine but the singleton
  evaluator materialized the scoped policy list once from the root scope, turning a per-request
  dependency like an EF `DbContext` into an undisposed, process-lifetime instance shared — unsafely,
  since `DbContext` is not thread-safe — across every concurrent write evaluation.
  - **Host impact:** none expected for hosts that resolve the evaluator (directly, or transitively
    through `ReviewGate`) from a request/turn scope, which is the pattern this framework's docs and
    tests already assume. Hosts that resolve `IApprovalPolicyEvaluator` from the *root* `IServiceProvider`
    (bypassing DI-provided scopes) must switch to resolving it from a scope — resolving a Scoped
    service from the root provider throws under `ValidateScopes` and is unsupported without it.
  - Lock test: `ServiceCollectionExtensionsTests.ApprovalPolicyEvaluator_WithScopedPolicyDependency_ResolvesUnderRealHostValidation`
    builds a provider with `ValidateScopes: true, ValidateOnBuild: true` — the exact settings a
    Development host applies — plus a Scoped `IApprovalPolicy` stub with its own Scoped dependency,
    and asserts construction succeeds, the evaluator resolves and evaluates inside a scope, and two
    separate scopes get distinct policy-dependency instances (guarding the concurrency hazard, not
    just the validation error).

### Added — Area-2 P2: tool-name and fabric-key parity checks; MAF tool-name override (affiant#16)

Implements the P2 wave of the Area-2 typed-contracts review
(`chancery docs/architecture-review/area-2-typed-contracts.md`) — two framework work items, both
additive, no breaking changes.

- **`ComplianceHarness.AssertToolNameRegistryParity` and `AssertFabricKeyParity`** — new public
  APIs on `Affiant.Testing.ComplianceHarness.ComplianceHarness`, generalizing the
  `AssertFieldSetParity` pattern (Area-1, P7) to two more boundaries the review's inventory flagged:
  LLM tool names (Area-2 gate ruling 2, "C-prime") and context-fabric keys. Both mirror
  `AssertFieldSetParity`'s shape — an explicit exemption-list parameter, precise failure messages
  naming the offending member, and a result record the caller asserts on
  (`ToolNameParityResult`/`FabricKeyParityResult`, plus a shared `ParityViolation(Member, Reason)`).
  Neither runs inside `ComplianceHarness.Verify` — both are opt-in, called directly. Because this
  package depends only on `Affiant.Abstractions`/`Affiant.Core` (deliberately — see the framework
  spec's rationale for why one compliance suite runs against both interception backends), neither
  method reflects over `[KernelFunction]` or an `AffiantToolCatalog` itself; the caller performs
  that one adapter-specific step and passes the resulting name list in (XML docs on each method
  spell out the SK and MAF acquisition one-liners). `AssertToolNameRegistryParity` is capable of
  replacing a host's bespoke `ToolNamesExhaustivenessTests` reflection test without losing any
  assertion (verified against the Area-2 P1 host-apps pattern). `AssertFabricKeyParity`'s live-key
  set is an honest caller-supplied enumeration, not runtime introspection — fabric keys have no
  central registry to reflect over the way tool names do (see the method's XML docs for the
  tradeoff). Covered by unit tests including mutation-style negative cases (a rogue exposed name,
  an orphaned constant, and — tool-name check only — two tools colliding on one name — each fail
  with a message naming the offending member).
- **`[AffiantToolName]` (Affiant.AgentFramework, affiant#16)** — new
  `Affiant.AgentFramework.Attributes.AffiantToolNameAttribute`, honored by
  `AffiantToolCatalog.FromType<T>()`. Lets a MAF tool method's LLM-visible name differ from its C#
  method name — the MAF counterpart to Semantic Kernel's `[KernelFunction("name")]` override —
  closing the gap that forced the Area-2 P1 Meridian branch to rename C# methods themselves to
  snake_case just to get a `ToolNames`-constant-backed name. The override flows into both the
  produced `AIFunction.Name` and the `AffiantToolDescriptor.FunctionName`. Two methods resolving to
  the same effective name (an override colliding with another method's name, or two overrides
  sharing a value) throws `InvalidOperationException` naming the collision at catalog-build time,
  not silently. Documented in `docs/adapters/microsoft-agent-framework.md` §4 and the new
  `docs/tool-authoring-guide.md` §10 (SK vs MAF naming, side by side).
  - **Behavior change, no-override path:** `AffiantToolDescriptor.FunctionName` now always equals
    the produced `AIFunction.Name`, including whatever sanitization
    `Microsoft.Extensions.AI`'s `AIFunctionFactory.Create` applied — most notably its conditional
    strip of a trailing `Async` from `Task`/`ValueTask`/`IAsyncEnumerable`-returning methods.
    **Previously this repo's own comments and this changelog claimed the no-override path was
    "byte-identical" to pre-affiant#16 behavior — that was false.** The pre-affiant#16 descriptor
    sourced `FunctionName` from the raw C# `method.Name`, which silently diverged from the actual
    LLM-visible tool name for every no-attribute, Async-suffixed, Task-returning method: the
    descriptor said e.g. `FetchThingAsync` while the LLM and every invocation carried `FetchThing`.
    That divergence is now closed — the descriptor always mirrors the name the LLM actually sees.
    **Action for hosts:** if anything keys on `AffiantToolDescriptor.FunctionName` for such a
    method (telemetry, the tool registry, extractor matching), it now sees the sanitized/stripped
    name instead of the raw method name. Either update those call sites to expect the stripped
    name, or add an explicit `[AffiantToolName(ToolNames.X)]` so the LLM-visible name — and hence
    the descriptor's `FunctionName` — is a literal you control rather than one derived from
    `AIFunctionFactory.Create`'s internal sanitization rules.

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

[Unreleased]: https://github.com/Sakwala/affiant/compare/v1.0.0-beta.1...HEAD
[1.0.0-beta.1]: https://github.com/Sakwala/affiant/releases/tag/v1.0.0-beta.1
