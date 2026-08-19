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

var response = await client.GetResponseAsync(messages, chatOptions);
```

`WithAffiant(...)` is the only supported way to attach Affiant here. It registers the catalog's tool descriptors, audits the tool list for hosted/provider-side tools Affiant cannot see (refusing by default — see `ExtensionsAIOptions.AcknowledgeUncoveredTools`), wraps every client-invoked `AIFunction`, and returns a **new** `ChatOptions`. Hosts must use the returned instance; the pre-wrap options bypass Affiant entirely.

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

- **Never call `WithAffiant` twice** over the same tools. The double-wrap guard detects this and throws at wire-up.
- **Never wire both this package and `Affiant.AgentFramework`** over the same tool catalog or chat-client pipeline. This one *cannot* be detected — the Agent Framework rewrites `ChatOptions.Tools` with its own private wrapper type per run, after this package's wire-up has already happened, and that type carries no marker either package can see. Pick one adapter per pipeline.

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
