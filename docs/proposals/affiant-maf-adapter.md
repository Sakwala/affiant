# Proposal: `Affiant.AgentFramework` — a Microsoft Agent Framework interception backend

> **Status:** Proposed, implementation in flight on branch `feat/agent-framework-adapter` · **Anchored:** 2026-07-05 · **Author:** Claude (AI conductor) for Seevali (maintainer) · **Companion:** [`affiant-maf-adapter-handoff.md`](affiant-maf-adapter-handoff.md) (context dossier — read it first if you have no prior Affiant context)

**What this document is.** The design for Affiant's second tool-interception backend: an adapter for the **Microsoft Agent Framework (MAF)** — Microsoft's successor to Semantic Kernel (SK), GA since 2026-04-03 — sitting as a peer beside the existing `Affiant.SemanticKernel` adapter behind one backend-neutral interception pipeline. It is written to be actionable from the public repo's file system alone; where private planning documents are cited, their load-bearing content is inlined here.

**Cold-start reading order**

1. [`affiant-maf-adapter-handoff.md`](affiant-maf-adapter-handoff.md) — who/why/state-of-the-world dossier for this proposal.
2. This file — the design.
3. [`../../README.md`](../../README.md) — Affiant positioning, including the MAF paragraph and the hosted-tool coverage scoping (§"Positioning").
4. [`../affiant-framework-specification.md`](../affiant-framework-specification.md) — the framework spec; most relevant: §3.12 (SK adapter surface + canonical pipeline order), §4 (six-layer dependency graph), §6 (the Seven Normative Rules).
5. [`../tool-authoring-guide.md`](../tool-authoring-guide.md) — the plugin/filter authoring patterns hosts use today.

---

## 1. Why a MAF adapter, and why now

**Semantic Kernel is in maintenance mode.** Microsoft's Agent Framework team stated (devblog, 2025-10-07, "Semantic Kernel and Microsoft Agent Framework") that new feature investment goes to MAF while SK receives critical-bug and security fixes, with support guaranteed "≥ 1 year after MAF GA." MAF GA'd **2026-04-03** (devblog "Microsoft Agent Framework Version 1.0"), so the SK support **floor** is ≈ **2027-04** — a floor, not a published end date. Verified against Microsoft primary sources on 2026-07-04 (memo in the private planning repo; verdicts inlined throughout this doc).

**Consequence:** targeting MAF is a *forward hedge, not an emergency*. Affiant's SK backend remains fully supported and first-class through the support window and beyond; the MAF adapter exists so that (a) new .NET agent projects — which Microsoft now steers to MAF — can adopt Affiant, and (b) Affiant's interception thesis is proven portable before SK's sunset ever becomes concrete.

The decision to dual-target — "Decision 5" (dual-target SK + MAF) in the beta readiness plan, a **private, non-essential planning document**; its full load-bearing content is inlined in this paragraph, so the private doc is not required to act on this proposal — was taken 2026-06-24 with its tradeoff explicit: a second backend roughly doubles interception maintenance. The accepted guardrail — and the heart of this design — is that **both backends must sit behind one abstraction and share all field-tagging logic**, so semantic drift between backends is structurally impossible rather than test-luck.

## 2. The MAF interception seam (API surface verified 2026-07-05)

Package: **`Microsoft.Agents.AI` 1.13.0** (published 2026-07-03; targets net8.0/net9.0/net10.0/netstandard2.0/net472; depends on `Microsoft.Extensions.AI(.Abstractions)` ≥ 10.6.0). MAF's function-calling middleware is the near-1:1 successor of SK's function-invocation filters, with three differences that shape this design:

1. **Registration is a builder decoration, not DI.** Middleware attaches with `baseAgent.AsBuilder().Use(middleware).Build()`, producing a **new wrapped `AIAgent`** — the original is untouched. Function-calling middleware is only supported on agents backed by `FunctionInvokingChatClient` (e.g. `ChatClientAgent`). Any code path that holds the *unwrapped* agent silently bypasses Affiant — the wiring API in §4.5 is shaped to prevent that.

2. **The middleware's return value *is* the function result.** The delegate signature is:

   ```csharp
   async ValueTask<object?> Middleware(
       AIAgent agent,
       FunctionInvocationContext context,   // Microsoft.Extensions.AI
       Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
       CancellationToken cancellationToken)
   ```

   .NET's `FunctionInvocationContext` exposes `.Function` (`AIFunction`), `.Arguments` (`AIFunctionArguments` — an `IDictionary<string, object?>` that also carries `.Services`, an `IServiceProvider`), `.Terminate` (bool), `.FunctionCallIndex`, `.FunctionCount` — and **no settable `.Result`** (unlike SK, and unlike MAF's own Python API). Where Affiant's SK filters seal evidence by mutating `context.Result`, the MAF bridge seals by **returning the (possibly replaced) value from the delegate**.

3. **`.Terminate` is a blunt instrument.** Microsoft documents that setting it skips the follow-up model request, may skip sibling function calls in the same iteration, and can leave chat history inconsistent. The review gate on MAF therefore prefers *result replacement* (return a rejection envelope the model can read) over termination; `.Terminate` is reserved for the cases where the SK backend would terminate the auto-invoke loop today.

**Session primitive:** conversations use `AgentSession` (`agent.CreateSessionAsync()`, `agent.RunAsync(input, session)`, `RunStreamingAsync(...)` → `IAsyncEnumerable<AgentResponseUpdate>`). ⚠️ Pre-GA samples used a type named `AgentThread`; it was **removed before GA** (≈ 2026-02). Any doc or sample referencing `AgentThread` is stale — do not code against it.

**The coverage boundary (load-bearing, verified against Microsoft primary docs 2026-07-04):** MAF's function-calling middleware fires **only for client/locally-invoked tools** — function tools and *local* MCP tools. **Hosted/provider-side tools bypass it entirely**: hosted MCP, code interpreter, web search, file search, and other server-executed toolboxes run on the provider's infrastructure and never enter the client middleware pipeline. Affiant on MAF can therefore swear to writes made by locally-invoked tools *only*. §4.6 makes this boundary structural; every documentation surface states it plainly.

## 3. Current state of the framework (anchored 2026-07-05) — the debt this design confronts

The repo ships eight co-versioned packages (`1.0.0-alpha.1`; the `v1.0.0-beta.1` publish is pending as an operator-only step). Interception today:

- `Affiant.SemanticKernel` holds three SK filters: `ToolArgumentCaptureFilter` (pre-tool; tags LLM-supplied arguments into the `ContextFabric` via `ProvenanceTag.FromTool`), `InferenceTriggerFilter` (pre-tool; runs task inference for write-intent tools), `ReviewGateFilter` (post-tool, `IAutoFunctionInvocationFilter`; routes `WriteProposal` envelopes through `ReviewGate.FileReviewAsync`).
- **But the fabric is not confined to the adapter package.** `Affiant.Core` itself implements SK filter interfaces directly in `ContextExtractor` (the abstract base hosts subclass), `TaskInferenceMergeFilter`, `ToolErrorFilter`, `ToolTracingFilter`, and `DeterministicShortCircuit`; `TaskInferenceRunner` takes an SK `ChatHistory` parameter. `Affiant.Core.csproj` carries a direct `Microsoft.SemanticKernel` PackageReference — in violation of the spec's own architectural constraint (§3.12.3, "L2 AC #4," ratified 2026-05-05: *Core must not take a direct SK dependency*).
- `Affiant.Abstractions` also references SK: `IChatSessionStore` is typed to SK's `ChatMessageContent`, and `InferenceCompletionRequest` / `InferenceFixtureCase` carry SK `ChatHistory` fields.

So the earlier working assumption that "the MAF port is confined to one package" (README §Positioning; beta plan §2 — the beta plan is a private, non-essential document, cited here for provenance only) is **false as written**. This proposal treats that not as scope creep but as the actual work: the violation is precisely what stands between the current code and a second backend. What *is* already backend-neutral and reusable as-is: `ProvenanceTag`/`ProvenanceChain`, `ContextFabric`, `TaskInferenceStep` (the confidence-merge algorithm, documented SK-free), `SchemaDrivenAffidavitProjection`, `IAffiantToolRegistry`/`AffiantToolDescriptor`, and the `Affiant.Testing.ComplianceHarness` (which depends only on abstractions such as `IInferenceCompletionPort`).

The canonical 7-step pipeline order (spec §3.12.4, enforced by `AffiantFilterPipelineOrderTests`) is the semantic contract both backends must reproduce: tool-error wrapping → context extraction → argument capture → inference trigger → deterministic short-circuit → inference merge → review gate.

## 4. Design

### 4.1 Principle: one pipeline, two bridges

All Affiant interception logic is defined **once**, backend-neutrally, in `Affiant.Core`, expressed against a neutral invocation contract in `Affiant.Abstractions`. Each backend package (`Affiant.SemanticKernel`, `Affiant.AgentFramework`) is a **thin bridge**: it translates its framework's native interception seam into the neutral contract, runs the one pipeline, and translates the outcome back. Bridges contain *no* provenance, inference, or review logic — if a bridge grows an `if` about field tagging, the design has been violated.

This is what makes the Decision-5 guardrail structural: there is exactly one place where pipeline order, tagging semantics, and review-gate behavior are defined, so the backends cannot drift apart (the failure class that produced the "hollow Affidavit" regression of 2026-04-30 — tracked by commit id `b72c1fa` in the **private** host-apps repo, not resolvable from this public repo — green structural tests over semantically lossy behavior — cannot recur *between backends*).

### 4.2 The neutral contract (new, in `Affiant.Abstractions`)

Sketch — naming may be polished during implementation; the **semantics are normative**:

```csharp
namespace Affiant.Abstractions.Models;

public sealed class ToolInvocationContext
{
    public required string FunctionName { get; init; }
    public required string PluginName { get; init; }
    public required IDictionary<string, object?> Arguments { get; init; }  // mutable pre-invocation
    public object? Result { get; set; }        // readable and replaceable post-invocation
    public bool Terminate { get; set; }        // backend maps to its native termination
    public required IServiceProvider Services { get; init; }  // scoped per invocation
}
```

```csharp
namespace Affiant.Abstractions.Interfaces;

public interface IToolInvocationFilter
{
    Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default);
}
```

Normative semantics:

- **Onion execution.** Filters wrap `next` in the canonical order of spec §3.12.4; code before `await next(...)` is pre-invocation, code after is post-invocation. The order-enforcement tests move to `Affiant.Core` and become backend-independent.
- **Result replacement.** After `next` completes, `context.Result` holds the tool's produced value; a filter may replace it (tool-error envelopes, review-gate annotations). The *backend bridge* is responsible for making the final `Result` the value its framework reports (SK: assign to the SK context; MAF: return it from the middleware delegate).
- **Termination.** `Terminate = true` requests that the backend stop the auto-invocation loop after this call. Bridges map it to their native flag; on MAF, filters should prefer result replacement (see §2 point 3) — the review gate only terminates where its SK behavior does today.
- **Scoped services.** Each invocation runs in a DI scope (today `ReviewGateFilter` opens one ad hoc; the pipeline runner owns this henceforth), so EF-backed stores and `IReviewContextProvider` resolve correctly on both backends.

### 4.3 `Affiant.Core` becomes genuinely backend-neutral

- The logic of `ToolErrorFilter`, `ToolTracingFilter`, `DeterministicShortCircuit`, `TaskInferenceMergeFilter`, the `ContextExtractor` base class, plus `Affiant.SemanticKernel`'s `ToolArgumentCaptureFilter`, `InferenceTriggerFilter`, and `ReviewGateFilter`, is re-expressed as `IToolInvocationFilter` implementations in `Affiant.Core` (the three SK-package filters move; the SK package keeps only bridging). A `ToolInvocationPipeline` runner in Core executes the ordered chain.
- `TaskInferenceRunner` loses its SK `ChatHistory` parameter in favor of the neutral message list below.
- **De-SK-ing `Affiant.Abstractions`:** a minimal neutral message record (sketch: `AffiantChatMessage(string Role, string Content)` — only what Affiant actually consumes) replaces SK types in `IChatSessionStore`, `InferenceCompletionRequest`, and `InferenceFixtureCase`. Each backend converts to/from its native message type at its own edge.
- The `Microsoft.SemanticKernel` PackageReferences are **removed** from `Affiant.Abstractions` and `Affiant.Core`, restoring L2 AC #4. A dependency test should enforce it from now on (the existing Story-6.11 static-analysis guard covers Affiant-package references only — extend it to forbid SK/MAF references in Abstractions/Core).
- `IToolInvocationCapture` (declared in Abstractions, consumed by nothing) is **removed** — the neutral filter contract subsumes its purpose. Pre-1.0 policy: the removal is the change.
- ⚠️ Spec/code drift discovered during recon, fixed alongside: spec §3.8 documents an `IIntentInterceptor` shape (`Priority`/`CanHandle(string)`) that does not match the shipped interface (dictionary-of-arguments). The shipped shape is authoritative; the spec text gets corrected (§7 below).

### 4.4 The SK bridge (`Affiant.SemanticKernel` after this change)

Keeps its public host-facing surface (`AddAffiantSemanticKernel`, `AddAffiantInferenceOrchestration`, `AddAffiantPluginsFromType<T>`, connectors, `ChatCompletionFactory`, capability registry) but internally becomes translation only: SK's `IFunctionInvocationFilter` and `IAutoFunctionInvocationFilter` implementations construct `ToolInvocationContext`s and run the neutral pipeline, preserving the exact two-interface split and firing positions the current filters have (pre-tool steps in the invocation filter; merge + review gate at the auto-invocation positions, where SK exposes loop termination). Host-visible renames (e.g. registering neutral filter types instead of SK filter types) are mechanical and documented in the CHANGELOG; behavior parity is gated by the existing SK test suite and the compliance harness.

### 4.5 The new package: `Affiant.AgentFramework`

- **Name.** `Affiant.AgentFramework` — symmetric with `Affiant.SemanticKernel` (Microsoft product name minus vendor prefix). Rejected: `Affiant.MAF` (cryptic), `Affiant.MicrosoftAgentFramework` (reads as Microsoft-shipped). ⚠️ The NuGet ID is **not yet reserved**; reservation on nuget.org is a maintainer/operator act that must precede any publish.
- **Dependencies.** `Affiant.Core` (transitively `Affiant.Abstractions`) + `Microsoft.Agents.AI` (1.13.0 at time of writing). Target `net10.0` single-TFM per repo convention. Standard packaging gates apply (`Directory.Build.targets` enforces PackageId = project name, canonical repo URL, current version).
- **Wiring surface** (sketch):

  ```csharp
  services.AddAffiantAgentFramework(options => { ... });   // pipeline, registry, inference port

  AIAgent agent = new ChatClientAgent(chatClient, instructions)
      .WithAffiant(serviceProvider, tools: AffiantToolCatalog.FromType<WorkOrderTools>());
  ```

  `WithAffiant(...)` is the single blessed way to attach Affiant: it (a) registers the catalog's tool descriptors with `IAffiantToolRegistry`, (b) attaches the function-calling middleware via `AsBuilder().Use(...)`, and (c) runs the hosted-tool audit of §4.6 — returning the wrapped agent. Because wrapping produces a new agent, the API is deliberately shaped so hosts receive *only* the wrapped instance; docs warn that holding the pre-wrap agent bypasses provenance capture.
- **Tool registration parity.** `AffiantToolCatalog.FromType<T>()` reflects over a type's public methods once, producing both the `AIFunction`s (via `AIFunctionFactory.Create`, honoring `[Description]`) and the `AffiantToolDescriptor`s from `[AffiantWriteTool]` — the same attribute and registry the SK path uses. One reflection pass, one descriptor store, both backends.
- **The middleware** builds a `ToolInvocationContext` from MAF's `FunctionInvocationContext` (arguments dictionary shared by reference where safe; function/plugin names from the `AIFunction` and catalog), opens the DI scope, runs the neutral pipeline with `next` bound to MAF's `next`, and **returns `context.Result`** as the delegate's return value — the sealing point. `context.Terminate` maps to MAF's `.Terminate` with the §2 caveat honored.
- **Inference port.** `AgentFrameworkInferenceCompletionPort : IInferenceCompletionPort` implemented over `Microsoft.Extensions.AI.IChatClient` structured output — the MAF counterpart of `SemanticKernelInferenceCompletionPort` (same no-tool-recursion rule: inference calls must not route back through function invocation).
- **Not ported:** the SK package's `Connectors/` machinery (`ChatCompletionFactory`, per-provider capability classes, `ManualToolInvoker`). `Microsoft.Extensions.AI.IChatClient` is itself the provider abstraction MAF builds on, and `FunctionInvokingChatClient` normalizes auto-invocation across providers — recreating a provider factory would duplicate MAF's own job. Hosts construct their `IChatClient` with standard MAF/M.E.AI idioms.

### 4.6 Hosted tools: honesty made structural

At `WithAffiant(...)` time the adapter audits the agent's tool set. Any tool that is not a client-invoked `AIFunction` (i.e. hosted MCP, code interpreter, web/file search, other provider-side tools) is **uncovered** — Affiant cannot see, tag, or gate it.

- **Default: refuse.** `WithAffiant` throws with a message naming each uncovered tool and explaining the boundary.
- **Override: explicit acknowledgment.** `options.AcknowledgeUncoveredTools = ["code_interpreter", ...]` permits named hosted tools; each acknowledged tool emits a startup telemetry warning and is recorded so the acknowledgment is auditable.
- **Rationale.** Affiant's operating principle is *"Nothing commits without evidence. Nothing writes without approval."* A silently uncovered write path breaks that oath while the host believes it holds. Refusal-by-default makes the boundary structural (the same stance the Evidence Card takes on `Empty` provenance: loud, never silent); the acknowledgment list is itself a sworn statement by the operator.
- If tool enumeration at wrap time proves impossible for some agent shape, the wiring API must be narrowed so tools pass through Affiant (the catalog) — detection before first run is the invariant, not the mechanism.

### 4.7 Semantics mapping

| Concern | SK backend (today) | Neutral contract | MAF backend (new) |
|---|---|---|---|
| Pre-tool argument access | `FunctionInvocationContext.Arguments` | `Arguments` dict | `FunctionInvocationContext.Arguments` (`AIFunctionArguments`) |
| Result read/replace | mutate `context.Result` | `Result` get/set | **return value of middleware delegate** |
| Stop auto-invoke loop | `AutoFunctionInvocationContext.Terminate` | `Terminate` | `context.Terminate` (with documented caveats) |
| Registration | DI-registered filters on the Kernel | n/a (pipeline is internal) | `AsBuilder().Use(...)` inside `WithAffiant` |
| Per-invocation DI scope | ad hoc in `ReviewGateFilter` | pipeline runner owns the scope | pipeline runner owns the scope |
| Tool identity | `[KernelFunction]` + descriptor registry | `FunctionName`/`PluginName` + `IAffiantToolRegistry` | `AffiantToolCatalog` + same registry |
| Inference LLM call | `SemanticKernelInferenceCompletionPort` | `IInferenceCompletionPort` | `AgentFrameworkInferenceCompletionPort` |
| Session persistence | `IChatSessionStore` over SK `ChatMessageContent` | `IChatSessionStore` over `AffiantChatMessage` | conversion at MAF edge (`ChatMessage` ↔ neutral) |

## 5. Non-goals

- **No hosted-tool coverage.** Out of reach by MAF's architecture (§2); handled by refusal/acknowledgment, never claimed.
- **No SK deprecation.** SK stays a first-class backend through its support window and for as long as hosts run it; one of the two private validation hosts stays on SK deliberately.
- **No simultaneous dual-backend host.** Both adapters may coexist in a process (nothing forbids it) but making that pleasant is not a goal.
- **No Python/other-language adapters, no MAF workflow/orchestration features** (agent-run middleware, `IChatClient` middleware) beyond the function-calling seam — Affiant's thesis lives at tool invocation.

## 6. Testing strategy

- **Pipeline order and semantics tests move to `Affiant.Core`** and run backend-free against the neutral pipeline (order, result replacement, terminate propagation, scope lifetime, tool-error enveloping).
- **Bridge translation tests per backend:** SK bridge tests assert parity with today's filter behavior (the existing `Affiant.SemanticKernel.Tests` suite is the regression baseline); `Affiant.AgentFramework.Tests` (new, 1:1 test-project convention, referencing `Affiant.TestInfrastructure`) exercises the middleware against a scripted `IChatClient`/`FunctionInvokingChatClient` — including a **multi-middleware onion-order test**, since Microsoft's docs do not specify chaining semantics for multiple `.Use(...)` calls; this must be pinned by an executable test, not an assumption.
- **Compliance parity:** `ComplianceHarness.Verify` already depends only on abstractions. Add a provider-factory `[Theory]` suite (the `DocketStoreProviderFactory` shape: `IEnumerable<object[]>` yielding `(IInferenceCompletionPort, providerName)` for SK and MAF) so every fixture case runs against both backends and `AssertProvenanceIsSubstantive` gates both — the hollow-Affidavit guard (private-repo commit `b72c1fa`, 2026-04-30 regression), now cross-backend.
- **Hosted-tool audit tests:** refusal on unacknowledged hosted tool; acknowledgment allows + warns; audit fires before first run.
- Build gates unchanged: `dotnet build/test Affiant.slnx -c Release` warning-clean (TreatWarningsAsErrors), `dotnet pack` passes the packaging-NFR target.

## 7. Documentation shipped with this change

- **Spec amendments** (same PR as implementation): correct §3.12.3's isolation claim to describe the neutral pipeline + bridges (L2 AC #4 becomes true and test-enforced); fix the §3.8 `IIntentInterceptor` drift; add the interception backend as **Seam 4** to §5's boundary contract; §3.12.4 pipeline order restated as backend-neutral; §4 package mapping gains the ninth package.
- **Adapter guide:** `docs/adapters/microsoft-agent-framework.md` — host-facing usage (wiring, catalog, hosted-tool acknowledgment, migration notes from an SK host), mirroring the tool-authoring guide's style.
- **README:** positioning paragraph updated from "a MAF adapter is planned" to present tense, with the §4.5 caveat that the port was *not* confined to one package and what was actually done; coverage scoping language unchanged (already correct).
- **CHANGELOG:** the public-surface renames for SK hosts, called out as pre-1.0 clean breaks.
- Website (affiant.dev) pages ship separately in that repo, framed against merge state honestly (repo-available; NuGet publish follows the next release).

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Dual-backend maintenance doubles drift surface | One pipeline, two bridges (§4.1); cross-backend ComplianceHarness `[Theory]` gate (§6) |
| MAF is shipping breaking changes at 1.x cadence (five `[BREAKING]`-flagged releases between 1.9–1.13, none touching this seam) | Pin 1.13.0; adapter's MAF surface is deliberately tiny (one middleware, one port, catalog); releases-page check is a standing step in dependency bumps |
| `.Terminate` on MAF can corrupt history / skip siblings | Review gate prefers result replacement; terminate used only where SK terminates today; covered by bridge tests |
| Refactor breaks existing SK hosts | Pre-1.0 clean-break policy; SK suite + compliance harness are the parity gate; renames documented in CHANGELOG |
| NuGet ID squatting before reservation | Maintainer reserves `Affiant.AgentFramework` (operator act) before any publish; until then the package exists only in-repo |
| Wrapped-agent bypass (host holds pre-wrap `AIAgent`) | `WithAffiant` is the only documented wiring; docs warn explicitly; consider an analyzer post-1.0 (not now — no speculative machinery) |

## 9. Open decisions (maintainer)

1. **Confirm or veto the hosted-tool default-refuse posture** (§4.6). Ship-blocking only for the MAF package.
2. **Reserve the `Affiant.AgentFramework` NuGet ID** — timing is the maintainer's, but before any publish that includes the package.
3. Whether the ninth package joins the co-versioned set for the *next* published version or stays repo-only until GA (the vision positions MAF dual-target as a GA deliverable; the beta ships SK-only either way).

## 10. Glossary

- **Affiant** — this framework: every AI-proposed write becomes an *Affidavit* (a sworn, per-field provenance record) reviewed by a human via an *Evidence Card*; a durable review queue (*Docket*) holds pending writes. Tagline: "Sworn provenance for every AI write."
- **SK / Semantic Kernel** — Microsoft's earlier .NET AI orchestration SDK; Affiant's first interception backend (function-invocation filters).
- **MAF / Microsoft Agent Framework** — Microsoft's successor agent SDK (GA 2026-04-03); .NET package `Microsoft.Agents.AI`, core types in `Microsoft.Extensions.AI`.
- **Interception backend** — the framework-specific seam where Affiant observes tool calls: argument capture before execution, result envelopes after, review gating on write intent.
- **ContextFabric** — Affiant.Core's store of field-level values with provenance chains, populated deterministically by filters ("code observes; models propose").
- **ToolEnvelope / WriteProposal** — the closed union every tool returns; write-intent tools return a `WriteProposal` carrying the Affidavit; actual writes happen only after review via the host's `IWriteExecutor` (Rule 3: write tools never write).
- **ReviewGate / Docket / Evidence Card** — the review machinery: filing, policy evaluation (standing order / referral / reviewer confirmation), human decision transport.
- **ComplianceHarness** — `Affiant.Testing.ComplianceHarness`: CI gate asserting Affidavits are substantive (no populated value tagged `ProvenanceSource.Empty`), guarding the hollow-Affidavit regression class (2026-04-30: a lossy extraction change kept structural tests green while shipping hollow Affidavits; tracked by private-repo commit `b72c1fa`, not resolvable from this public repo).
- **L2 AC #4** — architectural constraint ratified 2026-05-05: `Affiant.Core` must not take a direct SK dependency. Violated in code until this change; restored and test-enforced by it.

## 11. External dependencies this proposal presumes

- **.NET SDK 10.0.1xx** (pinned by `global.json`), xUnit, the repo's standard `dotnet build/test/pack` gates.
- **`Microsoft.Agents.AI` 1.13.0** + transitives (`Microsoft.Extensions.AI` ≥ 10.6.0) — added by this work to the new package only.
- **Microsoft Learn documentation** (middleware, tools, migration guide) — cited inline in §2; re-verify member names against the live API reference if implementation happens against a newer MAF version.
- No custom scripts, agents, or MCP servers are required to act on this proposal.
