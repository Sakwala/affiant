# Affiant.SemanticKernel

Semantic Kernel adapter for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Wires the Affiant filter pipeline (structured task inference, deterministic context extraction, review gating, structured error handling) into any Semantic Kernel host application, and provides per-provider connector capability detection with a manual tool-invocation fallback for providers without native auto-function-calling support.

## Quick start

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantSemanticKernel();
```

`AddAffiantSemanticKernel()` registers the SK filter pipeline in its canonical order (error handling → deterministic short-circuit → context extraction → argument capture → inference trigger → post-tool merge → review gate), the connector `CapabilityRegistry`, and the `ManualToolInvoker` fallback. Write tools are declared with the `[AffiantWriteTool]` attribute and registered via `AddAffiantTool<TStrategy>()`; misregistration is a hard startup failure (`AffiantStartupException`), never a warning.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.SemanticKernel.Filters` | `AffiantFilterPipeline` — registers the SK filter pipeline in canonical order |
| `Affiant.SemanticKernel.Adapters` | Inference orchestration adapters (`SemanticKernelInferenceCompletionPort` and pre-tool trigger filters) |
| `Affiant.SemanticKernel.Connectors` | Per-provider connector capabilities, provider configuration, and manual tool-invocation fallback |
| `Affiant.SemanticKernel.Validation` | Startup validation — registry-vs-kernel cross-checks that fail the host fast on misconfiguration |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the seven normative rules
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
