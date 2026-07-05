---
title: Microsoft Agent Framework Adapter — Affiant.AgentFramework
version: 1.0
date: 2026-07-05
status: shipped on branch feat/agent-framework-adapter; NuGet ID reservation pending (see §9)
scope: Hosts wiring Affiant into a Microsoft Agent Framework (MAF) agent
audience: Developers who already read docs/tool-authoring-guide.md and are adding (or migrating
  to) a MAF host; no prior MAF-specific Affiant context assumed
related:
  - docs/proposals/affiant-maf-adapter.md (the design this guide documents; §2 verifies the MAF
    API surface, §4.6 is the hosted-tool honesty design)
  - docs/proposals/affiant-maf-adapter-handoff.md (context dossier — why a MAF adapter, at all)
  - docs/affiant-framework-specification.md §3.12.3 (neutral pipeline + backend bridges)
  - docs/tool-authoring-guide.md (the plugin/filter authoring patterns this guide assumes)
---

# Microsoft Agent Framework Adapter — `Affiant.AgentFramework`

## Contents

1. [What this package is](#1-what-this-package-is)
2. [Glossary](#2-glossary)
3. [Wiring a MAF host](#3-wiring-a-maf-host)
4. [`AffiantToolCatalog`](#4-affianttoolcatalog)
5. [Session persistence: message conversion](#5-session-persistence-message-conversion)
6. [The hosted-tool boundary](#6-the-hosted-tool-boundary)
7. [Migrating a Semantic Kernel host to MAF](#7-migrating-a-semantic-kernel-host-to-maf)
8. [Troubleshooting](#8-troubleshooting)
9. [External dependencies and current status](#9-external-dependencies-and-current-status)

---

## 1. What this package is

`Affiant.AgentFramework` is Affiant's second interception backend — an adapter for the
**Microsoft Agent Framework (MAF)**, Microsoft's successor to Semantic Kernel (SK), sitting
beside the existing `Affiant.SemanticKernel` adapter behind one shared, backend-neutral
interception pipeline (`docs/affiant-framework-specification.md` §3.12.3). If you already run
Affiant on an SK host, the provenance tagging, task inference, and review-gating behavior you get
from this package is *identical* — the same `Affiant.Core` filters run either way. What differs
is only how the framework attaches to your agent runtime.

**Read this guide if:** you are wiring a new MAF host, or migrating an existing SK host to MAF.

**Read `docs/tool-authoring-guide.md` first if:** you have never written an Affiant tool before.
This guide assumes you understand `ToolEnvelope`, `WriteProposal`, `Affidavit`,
`ITaskInferenceStrategy`, and `[AffiantWriteTool]` already — it does not re-teach them.

## 2. Glossary

- **MAF** — Microsoft Agent Framework. Microsoft's current-generation .NET agent SDK, GA since
  2026-04-03. NuGet package `Microsoft.Agents.AI` (this adapter targets `1.13.0`); core chat
  types live in `Microsoft.Extensions.AI`.
- **`AIAgent`** — MAF's agent abstraction. `ChatClientAgent` is the concrete implementation this
  adapter is built and tested against; it is also the only concrete `AIAgent` shape
  `Microsoft.Agents.AI` 1.13.0 ships.
- **`AIFunction`** — MAF's client-invoked tool type (`Microsoft.Extensions.AI`). Produced from a
  method via `AIFunctionFactory.Create`. Distinct from a *hosted tool* (§6).
- **Function-calling middleware** — MAF's tool-interception seam: a delegate attached via
  `agent.AsBuilder().Use(middleware).Build()` that wraps every `AIFunction` invocation. This is
  the MAF analog of SK's function-invocation filters, and the seam this adapter bridges.
- **`AgentSession`** — MAF's conversation-state primitive (`agent.CreateSessionAsync()`,
  `agent.RunAsync(input, session)`). ⚠️ Pre-GA MAF samples used a type named `AgentThread`; it
  was **removed before GA** (≈2026-02). Any sample or blog post referencing `AgentThread` predates
  GA and is not the current API — do not code against it (§8).
- **Neutral pipeline** — the single, backend-free implementation of Affiant's provenance tagging,
  task inference, and review gating, living in `Affiant.Core` against the
  `IToolInvocationFilter`/`ToolInvocationContext` contract in `Affiant.Abstractions`. Both this
  package and `Affiant.SemanticKernel` are thin translation bridges over the same neutral
  pipeline (`docs/affiant-framework-specification.md` §3.12.3). Neither bridge contains any
  provenance, inference, or review-gate logic of its own.
- **Hosted tool** — a tool the LLM provider executes on its own infrastructure (hosted MCP, code
  interpreter, web/file search, and similar) rather than one MAF invokes client-side. See §6.

## 3. Wiring a MAF host

Two DI calls plus one wrapping call attach Affiant to a MAF agent:

```csharp
using Affiant.AgentFramework;
using Affiant.AgentFramework.Extensions;
using Affiant.Core.Extensions;
using Microsoft.Agents.AI;

// 1. Register the neutral pipeline (Affiant.Core) and this backend's bridge.
builder.Services.AddAffiantCore();
builder.Services.AddAffiantAgentFramework();   // pipeline filters, IAffiantToolRegistry,
                                                // AgentFrameworkInferenceCompletionPort
builder.Services.AddSingleton<IChatClient>(/* your provider's IChatClient */);
builder.Services.AddScoped<WorkOrderTools>();  // your tool type; see §4

// 2. Build the underlying agent, then wrap it.
var serviceProvider = builder.Services.BuildServiceProvider();
var chatClient = serviceProvider.GetRequiredService<IChatClient>();
var catalog = AffiantToolCatalog.FromType<WorkOrderTools>();

AIAgent agent = new ChatClientAgent(
        chatClient,
        instructions: "You are a work-order assistant.",
        tools: catalog.Functions.Cast<AITool>().ToList(),
        services: serviceProvider)
    .WithAffiant(serviceProvider, catalog);
```

**`WithAffiant(...)` is the single blessed way to attach Affiant** to a MAF agent
(`src/Affiant.AgentFramework/Extensions/AgentExtensions.cs`). It:

1. Registers the catalog's `AffiantToolDescriptor`s with `IAffiantToolRegistry` (the same
   registry `Affiant.SemanticKernel` populates — one descriptor store for both backends).
2. Runs the hosted-tool coverage audit (§6) — before the agent's first turn, not lazily on first
   call.
3. Attaches `AffiantFunctionInvocationMiddleware` via `agent.AsBuilder().Use(...).Build(services)`.
4. Returns the **wrapped** agent.

**Wrapping produces a new `AIAgent` instance. The pre-wrap agent silently bypasses Affiant if a
host retains and calls it instead of the wrapped instance** — MAF's `AsBuilder().Use(...).Build()`
call does not mutate the original agent, it decorates it. Discard the unwrapped local, or shadow
it, so nothing in your codebase can accidentally call the version with no provenance tracking:

```csharp
// Wrong — the unwrapped `agent` variable is still callable and bypasses Affiant entirely.
var agent = new ChatClientAgent(chatClient, instructions, tools: ..., services: sp);
var wrapped = agent.WithAffiant(sp, catalog);
await agent.RunAsync(userMessage, session);       // BUG: no provenance captured, no review gate

// Right — only the wrapped instance exists in scope past the wiring line.
AIAgent agent = new ChatClientAgent(chatClient, instructions, tools: ..., services: sp)
    .WithAffiant(sp, catalog);
await agent.RunAsync(userMessage, session);       // Affiant's middleware is in the call chain
```

`AddAffiantAgentFramework()` is the MAF analog of SK's `AddAffiantSemanticKernel()` +
`AddAffiantInferenceOrchestration()` combined into one call: MAF has one function-calling seam,
not SK's invocation/auto-invocation split, so there is no reason to keep separate extension
methods for "pre-tool" and "post-tool" registration the way the SK package does.

## 4. `AffiantToolCatalog`

`AffiantToolCatalog.FromType<T>(pluginName: null)` (`src/Affiant.AgentFramework/AffiantToolCatalog.cs`)
reflects over `T`'s public instance methods **once**, producing both:

- The `AIFunction`s MAF invokes (`AIFunctionFactory.Create`, honoring `[Description]`).
- The `AffiantToolDescriptor`s the neutral pipeline reads (from `[AffiantWriteTool]` where
  present; a plain `Operation.ReadQuery` descriptor otherwise) — the same descriptor shape and
  attribute the SK adapter's plugin walker produces.

**MAF has no `[KernelFunction]`-equivalent marker attribute.** Unlike SK's plugin walker (which
only picks up `[KernelFunction]`-decorated methods), `FromType<T>()` reflects over *every* public
instance method on `T` except those declared on `object` and property/event accessors
(`MethodInfo.IsSpecialName`). Practical consequence: **a MAF tool type's public surface should
contain only tool methods** — an unrelated public helper method on the same class becomes a
callable `AIFunction` too.

**No "Async"-suffix stripping.** SK's plugin walker strips a bare trailing `Async` from a method
name when no explicit `[KernelFunction]` name is given; `AIFunctionFactory.Create` does not. A
method named `CreateWidgetAsync` becomes tool `CreateWidgetAsync` on MAF but tool `CreateWidget`
on an equivalent SK plugin. This is a permanent, structural naming asymmetry between the two
backends — if you want identical tool names across an SK and a MAF host from the same domain
type, avoid trailing `Async` in method names, or pass an explicit name at the `AIFunctionFactory`
call site (`FromType<T>` does not currently accept a per-method name override).

**Each `AIFunction` resolves its invocation target from `AIFunctionArguments.Services` per call**,
not from an instance the catalog holds. This is why `FromType<T>()` takes no instance and no
`IServiceProvider` parameter: register `T` in your own DI container (e.g.
`services.AddScoped<WorkOrderTools>()`) and construct the agent with
`new ChatClientAgent(chatClient, ..., services: serviceProvider)` — MAF threads that provider
through to every function invocation, and the catalog resolves `T` from it at call time. If `T`
is not resolvable, the produced `AIFunction` throws `InvalidOperationException` naming the type
and the fix, at first invocation (not at catalog-build time).

**Plugin name.** `AffiantFunctionInvocationMiddleware` looks up the invoked function's
descriptor in `IAffiantToolRegistry` by function name to recover the plugin name for the neutral
pipeline — `FunctionInvocationContext` (MAF) has no plugin/namespace concept the way SK's
`KernelFunction.PluginName` does. If no descriptor is registered for a function name (a tool not
routed through `AffiantToolCatalog`), the plugin name falls back to `string.Empty`, which the
neutral filters treat as a wildcard.

## 5. Session-store message conversion

`Affiant.EntityFramework`'s `IChatSessionStore` implementations, and `InferenceCompletionRequest`,
are typed against the backend-neutral `AffiantChatMessage` record
(`src/Affiant.Abstractions/Models/AffiantChatMessage.cs`), not against any MAF or SK chat-message
type. `Affiant.AgentFramework`'s `MafMessageConversions`
(`src/Affiant.AgentFramework/Adapters/MafMessageConversions.cs`) converts at the MAF edge:

- `ToNeutral(IEnumerable<ChatMessage>)` — MAF's `Microsoft.Extensions.AI.ChatMessage` →
  `AffiantChatMessage`, carrying `Role`, `Content`/`Text`, and `AuthorName`.
- `ToChatMessages(IReadOnlyList<AffiantChatMessage>)` — the reverse, used when building the
  inference prompt (§`AgentFrameworkInferenceCompletionPort`).

`AffiantFunctionInvocationMiddleware` also populates `ToolInvocationContext.History` from
`FunctionInvocationContext.Messages` (the actual per-call message list MAF supplies) and
`TurnNumber` from `FunctionInvocationContext.Iteration` — both richer neutral-seam sources than the
SK bridge has (SK reads a host-populated `kernel.Data` side channel).

### 5.1 Conversation identity and idempotency wiring

`AffiantFunctionInvocationMiddleware` threads `ToolInvocationContext.ConversationId` from
**`FunctionInvocationContext.Options.ConversationId`** — the `Microsoft.Extensions.AI.ChatOptions`
conversation id the function-invoking chat client carries through a run. This is what gives
`InferenceTriggerFilter` a genuinely per-conversation idempotency namespace
(`(ConversationId, FunctionName, TurnNumber)`); without it the key collapses to a per-`IContextFabric`
fallback hash and can dedup across unrelated conversations.

**What the host must do:** set the conversation id on the run so MAF carries it onto `ChatOptions`.
The idiomatic path is the run/thread's conversation id — e.g. supply
`ChatClientAgentRunOptions { ChatOptions = new ChatOptions { ConversationId = "<your id>" } }` when
running the agent, or use your provider's server-side thread/conversation id if it round-trips
`ConversationId`. When the id is absent the middleware leaves `ConversationId` null and
`InferenceTriggerFilter`'s fabric-instance fallback applies — safe, but conservative.

**Do not rely on a shared fabric for isolation:** the conversation-scoped `IContextFabric` (registered
`Scoped` by `AddAffiantCore()`, see the tool-authoring guide §4.1) is the primary isolation mechanism.
The middleware runs the pipeline in the run's ambient scope when the host wired one onto
`AIFunctionArguments.Services`; otherwise the pipeline owns a fresh scope per tool invocation, so each
call gets its own fabric. Either way concurrent MAF runs never share fabric state. `AgentSession` in
`Microsoft.Agents.AI` 1.13.0 exposes no first-class session identifier (only an opaque
`AgentSessionStateBag`), so `ChatOptions.ConversationId` is the neutral-seam source of record.

## 6. The hosted-tool boundary

**Load-bearing limitation, stated plainly: Affiant on MAF swears only to writes made by
locally-invoked tools.**

MAF's function-calling middleware fires **only** for client/locally-invoked tools — function
tools (`AIFunction`) and local MCP tools. **Hosted/provider-side tools bypass it entirely**:
hosted MCP, code interpreter, web search, file search, and other server-executed toolboxes run on
the LLM provider's own infrastructure and never enter the client middleware pipeline at all.
There is no MAF extension point that would let Affiant observe them. If your agent has a hosted
tool that can write anywhere, **Affiant cannot see, tag, or gate that write** — full stop.

`WithAffiant(...)` makes this structural rather than a silent gap, via
`HostedToolAudit.Run` (`src/Affiant.AgentFramework/Validation/HostedToolAudit.cs`), which runs
before the agent's first turn:

- **Default: refuse.** If the wrapped agent's tool set (read via
  `agent.GetService(typeof(ChatOptions))`) contains any tool that is not an `AIFunction`,
  `WithAffiant` throws `InvalidOperationException` naming every uncovered tool.
- **Override: explicit acknowledgment.** `AgentFrameworkOptions.AcknowledgeUncoveredTools =
  ["code_interpreter", ...]` permits named hosted tools to pass. Each acknowledged tool emits a
  `agentframework.hosted_tool_acknowledged` telemetry span and an `ILogger` warning at wrap time,
  so the acknowledgment is auditable, never silent.

  ```csharp
  builder.Services.AddAffiantAgentFramework(options =>
  {
      options.AcknowledgeUncoveredTools = ["code_interpreter"];
  });
  ```

- **The audit itself can fail to enumerate an agent's tools.** `agent.GetService(typeof(ChatOptions))`
  answers non-null only for `ChatClientAgent` — the only concrete `AIAgent` shape
  `Microsoft.Agents.AI` 1.13.0 ships. If a host supplies some other `AIAgent` implementation where
  this probe returns `null`, Affiant cannot audit for uncovered hosted tools *at all*. By the same
  detection-before-first-run principle, `WithAffiant` refuses by default in this case too, unless
  the host sets `AgentFrameworkOptions.AllowUnauditableAgent = true` — mirroring
  `AcknowledgeUncoveredTools`'s shape exactly (explicit opt-in, an
  `agentframework.unauditable_agent_acknowledged` telemetry span, and an `ILogger` warning naming
  the agent's concrete type).

  ```csharp
  builder.Services.AddAffiantAgentFramework(options =>
  {
      options.AllowUnauditableAgent = true;   // only if you understand you get zero hosted-tool audit
  });
  ```

**Rationale.** Affiant's operating principle is "Nothing commits without evidence. Nothing writes
without approval." A silently uncovered write path breaks that oath while the host believes it
holds. Refusal-by-default makes the boundary structural — the same stance the Evidence Card takes
on `ProvenanceSource.Empty`: loud, never silent.

## 7. SK → MAF host migration notes

Moving an existing `Affiant.SemanticKernel` host to `Affiant.AgentFramework` (or running both
side by side, which nothing forbids) does not change any provenance, inference, or review-gate
behavior — the neutral pipeline in `Affiant.Core` is identical either way
(`docs/affiant-framework-specification.md` §3.12.3). What changes at the call site:

| Concern | SK host | MAF host |
|---|---|---|
| Tool registration | `[KernelFunction]` + `AddAffiantPluginsFromType<T>` | Every public method reflected by `AffiantToolCatalog.FromType<T>()` (§4) — no marker attribute |
| Attach Affiant | DI-registered `IFunctionInvocationFilter`/`IAutoFunctionInvocationFilter` on the `Kernel` | `agent.WithAffiant(services, catalog)` — a builder decoration producing a new `AIAgent` (§3) |
| DI setup | `AddAffiantSemanticKernel()` + `AddAffiantInferenceOrchestration()` | `AddAffiantAgentFramework()` (one call — MAF has no stage split) |
| Provider abstraction | `IChatCompletionService` + `ChatCompletionFactory`/`IConnectorCapabilities` (SK-specific connector quirks) | `Microsoft.Extensions.AI.IChatClient` — this *is* MAF's provider abstraction; no Affiant-side connector factory exists or is needed |
| Session state | SK `ChatHistory` (via `SessionRehydrator` in `Affiant.SemanticKernel`) | MAF `AgentSession` (`agent.CreateSessionAsync()`) |
| Hosted-tool coverage | Not separately audited (SK hosts document the same limitation in prose; MAF added a structural check, §6) | Structural, enforced at `WithAffiant` time (§6) |

**Not ported to MAF, deliberately:** the SK package's `Connectors/` machinery
(`ChatCompletionFactory`, per-provider `IConnectorCapabilities` implementations,
`ManualToolInvoker`). `Microsoft.Extensions.AI.IChatClient` is itself the provider abstraction MAF
is built on, and `FunctionInvokingChatClient` already normalizes auto-invocation across
providers — recreating a provider factory here would duplicate MAF's own job. Construct your
`IChatClient` with standard MAF/`Microsoft.Extensions.AI` idioms.

## 8. Troubleshooting

**"My tool calls aren't being tagged with provenance / no Evidence Card appears — but I called
`WithAffiant`."** You are almost certainly holding and calling the *pre-wrap* agent somewhere
(§3). Search your codebase for every place a `ChatClientAgent`/`AIAgent` local variable is
constructed and confirm the variable actually in use downstream is the `.WithAffiant(...)` return
value, not the constructor's return value. This is the single most common wiring mistake — MAF's
`AsBuilder().Use(...).Build()` decorates rather than mutates, unlike some middleware systems that
have call sites automatically pick up newly attached behavior.

**"I'm following a MAF sample and it references `AgentThread`, but that type doesn't exist in my
installed package."** The sample predates MAF's 2026-04-03 GA. `AgentThread` was removed before
GA (≈2026-02); the current session primitive is `AgentSession`
(`agent.CreateSessionAsync()` / `agent.RunAsync(input, session)`). Any blog post, Stack Overflow
answer, or AI-generated snippet referencing `AgentThread` is stale — do not adapt it as-is.

**"`WithAffiant` throws `InvalidOperationException` naming a hosted tool I didn't expect to be a
problem."** Read §6. The tool bypasses MAF's client middleware entirely, so Affiant genuinely
cannot see writes made through it — this is not a bug to work around, it is the honest boundary.
Either remove the tool, or acknowledge it explicitly via `AgentFrameworkOptions.AcknowledgeUncoveredTools`
if your host accepts the coverage gap for that specific tool.

**"`WithAffiant` throws naming my agent's concrete type, saying the audit can't enumerate its
tools."** Your `AIAgent` is not a `ChatClientAgent` and doesn't expose `ChatOptions` via
`GetService`. Set `AgentFrameworkOptions.AllowUnauditableAgent = true` only if you understand this
means *zero* hosted-tool coverage audit for that agent, not just for tools you'd expect to be
flagged.

**"A write tool call silently never executes — no exception anywhere — and my test doesn't
register `IChatClient`."** `AgentFrameworkInferenceCompletionPort` requires `IChatClient` to be
registered in DI the moment *any* registered tool carries `[AffiantWriteTool]` (i.e. has an
`InferenceStrategy`) — even in a test that never intends to exercise inference. This is because
resolving the filter list (`provider.GetServices<IToolInvocationFilter>()`) constructs
`InferenceTriggerFilter`, whose dependency chain reaches `AgentFrameworkInferenceCompletionPort`,
whose constructor requires `IChatClient`. That construction failure happens *before* any neutral
filter's own try/catch runs, so it propagates into `FunctionInvokingChatClient`, which silently
swallows function-invocation exceptions into an error `FunctionResultContent` and continues the
model loop — the write-tool call never runs, and no exception is visible anywhere in your test
output. Register `IChatClient` in every host and test DI container that uses
`AddAffiantAgentFramework()`, even ones that only care about read tools.

## 9. External dependencies and current status

- **`Microsoft.Agents.AI` 1.13.0** (target `net10.0`; depends on `Microsoft.Extensions.AI` ≥
  10.6.0) — the only third-party package this adapter adds.
- **.NET SDK 10.0.1xx**, pinned by the repo's `global.json`.
- **The `Affiant.AgentFramework` NuGet package ID has not yet been reserved on nuget.org** as of
  2026-07-05. The package exists on branch `feat/agent-framework-adapter` in this repo and builds
  and tests as part of the solution, but is not yet published; reserving the ID and joining the
  co-versioned publish set are maintainer/operator acts tracked in
  `docs/proposals/affiant-maf-adapter.md` §9.
- No custom scripts, external agents, or MCP servers are required to use this package.
