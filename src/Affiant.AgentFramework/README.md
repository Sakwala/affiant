# Affiant.AgentFramework

Microsoft Agent Framework (MAF) adapter for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Bridges MAF's function-calling middleware (`Microsoft.Agents.AI` / `Microsoft.Extensions.AI`) into Affiant's backend-neutral tool-invocation pipeline (the same pipeline the `Affiant.SemanticKernel` adapter runs), so hosts building on MAF get the same provenance tagging, task inference, and review-gate behavior as SK hosts — for tools MAF's client-side middleware can see. See [`docs/adapters/microsoft-agent-framework.md`](https://github.com/Sakwala/affiant/blob/main/docs/adapters/microsoft-agent-framework.md) for the coverage boundary (hosted/provider-side tools bypass MAF's middleware entirely) and full wiring guide.

## Quick start

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantAgentFramework();

var catalog = AffiantToolCatalog.FromType<MyTools>();
AIAgent agent = new ChatClientAgent(chatClient, instructions: "...", tools: catalog.Functions, services: serviceProvider)
    .WithAffiant(serviceProvider, catalog);
```

`WithAffiant(...)` is the only supported way to attach Affiant to an `AIAgent`: it registers the catalog's tool descriptors, attaches the neutral pipeline as MAF function-calling middleware, audits the agent's tool set for hosted/provider-side tools Affiant cannot see (refusing by default — see `AgentFrameworkOptions.AcknowledgeUncoveredTools`), and returns the wrapped agent. Hosts must use the returned instance; the pre-wrap agent bypasses Affiant entirely.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.AgentFramework` | `AffiantToolCatalog` — one reflection pass over a tool type producing both `AIFunction`s and `AffiantToolDescriptor`s |
| `Affiant.AgentFramework.Filters` | `AffiantFunctionInvocationMiddleware` — translates MAF's `FunctionInvocationContext` into the neutral pipeline and back |
| `Affiant.AgentFramework.Extensions` | `WithAffiant` wiring extension, `AddAffiantAgentFramework` DI registration |
| `Affiant.AgentFramework.Adapters` | `AgentFrameworkInferenceCompletionPort` — structured-output inference over `IChatClient` |
| `Affiant.AgentFramework.Validation` | Hosted-tool coverage audit |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the seven normative rules
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair
- [`Affiant.AgentFramework` proposal](https://github.com/Sakwala/affiant/blob/main/docs/proposals/affiant-maf-adapter.md) — the design this package implements

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
