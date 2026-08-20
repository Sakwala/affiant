# Affiant.Extensions.AI

Microsoft.Extensions.AI adapter for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Bridges Microsoft.Extensions.AI's own function-calling seam (`IChatClient` + `FunctionInvokingChatClient` + `AIFunction`) into Affiant's backend-neutral tool-invocation pipeline — the same pipeline the `Affiant.SemanticKernel` and `Affiant.AgentFramework` adapters run — so hosts building directly on Microsoft.Extensions.AI get the same provenance tagging, task inference, and review-gate behavior, without adopting an agent framework.

**Provider-neutral by construction.** This package references `Microsoft.Extensions.AI` and nothing else — no `Microsoft.Agents.AI`, no provider client. Bring your own `IChatClient` (`Microsoft.Extensions.AI.OpenAI`, a Gemini client, Ollama, a custom one); Affiant never sees which.

## Quick start

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantExtensionsAI();

// The chat client must have UseFunctionInvocation() — that is the client that runs the tool loop.
IChatClient client = new ChatClientBuilder(innerClient)
    .UseFunctionInvocation()
    .Build(serviceProvider);

var catalog = AffiantToolCatalog.FromType<MyTools>();

var chatOptions = new ChatOptions { Tools = [.. catalog.Functions] }
    .WithAffiant(serviceProvider, catalog);

// Required, not optional — see "Set ConversationId" below.
chatOptions.ConversationId = conversationId;

var response = await client.GetResponseAsync(messages, chatOptions);
```

`WithAffiant(...)` is the only supported way to attach Affiant here. It registers the catalog's tool descriptors, audits the tool list for hosted/provider-side tools Affiant cannot see (refusing by default — see `ExtensionsAIOptions.AcknowledgeUncoveredTools`), wraps every client-invoked `AIFunction`, and returns a **new** `ChatOptions`. Hosts must use the returned instance; the pre-wrap options bypass Affiant entirely.

## Set `ConversationId` — omitting it silently degrades inference

Affiant runs task inference **once** per `(conversation, tool, turn)`. When `ChatOptions.ConversationId` is null, there is no conversation to key on, so the idempotency key falls back to the identity of the conversation-state object (`IContextFabric`) instead.

At this seam that object is **process-global**. `FunctionInvokingChatClient` hands Affiant the provider the `ChatClientBuilder` was built from — your application root — so the scoped `IContextFabric` resolves to a single shared instance rather than one per conversation. Every conversation therefore collapses onto the same key, and **the second and all later conversations skip write-tool inference entirely**: no exception, no warning, just affidavits built from the raw tool arguments with nothing inferred. Setting `ConversationId` per conversation restores correct behaviour and costs one line.

This limitation is shared by the `Affiant.SemanticKernel` and `Affiant.AgentFramework` adapters — all three source their ambient provider the same way — and the framework-level fix (a per-turn scope) is tracked separately. Until then, set `ConversationId`. Both the failure and the mitigation are pinned by `ConversationScopeBleedAtTheSeamTests`.

## How interception works

Affiant intercepts a tool by *being* it. Each `AIFunction` is wrapped in an `AffiantDelegatingAIFunction` (a `Microsoft.Extensions.AI` `DelegatingAIFunction`) whose invocation runs the whole neutral filter onion around the real tool body, reading the per-call `FunctionInvokingChatClient.CurrentContext` for iteration, message history and conversation id, and writing `Terminate` back to it to end the turn.

This is the same mechanism the Microsoft Agent Framework uses internally for its own function-invocation middleware, and it is deliberately *not* the `FunctionInvokingChatClient.FunctionInvoker` delegate: that delegate is last-write-wins and silently no-ops if a host forgets to configure it, whereas a wrapper cannot be bypassed — even a custom loop calling `AIFunction.InvokeAsync` directly still passes through Affiant.

## Coverage boundary

| Tool kind | Covered? |
|---|---|
| `AIFunction` (client-invoked) | Yes — wrapped, fully gated |
| `HostedWebSearchTool`, `HostedCodeInterpreterTool`, `HostedFileSearchTool`, `HostedMcpServerTool`, `HostedImageGenerationTool`, `HostedToolSearchTool` | **No** — provider-executed markers with no client-side invocation to wrap |

Uncovered tools make `WithAffiant` throw at wire-up, naming each one. Acknowledge them explicitly via `ExtensionsAIOptions.AcknowledgeUncoveredTools` if the host accepts the gap; each acknowledgment emits a telemetry span and a warning, so it is auditable and never silent.

## One Affiant adapter per tool catalog

Affiant's neutral pipeline is **not idempotent**: running it twice for one logical tool call double-tags provenance, fires task inference twice, and files the same write proposal onto the docket twice — a silent semantic corruption, not an error. So:

- **Never call `WithAffiant` twice** over the same tools.
- **Never wire both this package and `Affiant.AgentFramework`** over the same tool catalog or chat-client pipeline. Pick one adapter per pipeline.

Two guards enforce this, and it is worth knowing which one catches what:

| Guard | When | Catches | Misses |
|---|---|---|---|
| Wire-up marker | `WithAffiant`, before anything is registered | An Affiant wrapper sitting **directly** on `ChatOptions.Tools` | Any wrapper hidden behind another `DelegatingAIFunction` — host telemetry/retry/redaction middleware, or the Agent Framework's own per-run wrapper |
| Invoke-time re-entrancy guard | First nested tool invocation | Every nesting shape, at any depth, including the cross-adapter case | Nothing in this class — but it fails the call rather than the wire-up, so the mistake surfaces later |

The wire-up guard is the friendlier of the two: it throws before a turn can run, and a refused wiring is a pure no-op. The invoke-time guard is the backstop for what a top-level type test structurally cannot see. If you hit it, the fix is always the same — call `WithAffiant` exactly once, on the unwrapped catalog, and use only the `ChatOptions` it returns.

A tool body that starts its **own** governed sub-agent is not double-wrapping and is explicitly allowed: that sub-agent's `FunctionInvokingChatClient` publishes its own invocation context, so its tools run their own onion normally.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.Extensions.AI` | `AffiantToolCatalog` — one reflection pass over a tool type producing both `AIFunction`s and `AffiantToolDescriptor`s |
| `Affiant.Extensions.AI.Filters` | `AffiantDelegatingAIFunction` — the wrapper that runs the neutral pipeline at Microsoft.Extensions.AI's function-calling seam; `IAffiantWrappedFunction` — the double-wrap marker |
| `Affiant.Extensions.AI.Extensions` | `WithAffiant` wiring extension, `AddAffiantExtensionsAI` DI registration, `ExtensionsAIOptions` |
| `Affiant.Extensions.AI.Adapters` | `ExtensionsAIInferenceCompletionPort` — structured-output inference over `IChatClient` |
| `Affiant.Extensions.AI.Validation` | Hosted-tool coverage audit |
| `Affiant.Extensions.AI.Attributes` | `[AffiantToolName]` — LLM-visible tool-name override |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the seven normative rules
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
