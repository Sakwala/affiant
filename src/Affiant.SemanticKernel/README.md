# Affiant.SemanticKernel

Semantic Kernel adapter for the [Affiant framework](https://github.com/affiant-dev/affiant).

Wires the Affiant filter pipeline (task inference, context extraction, review gating, error handling) into any SK-based host application via a single `AddAffiantSemanticKernel()` call.

> **Note**: This package is under active development (Phase 2 — Story 9.x). Full documentation will be added in Story 9.3 when the `AddAffiantSemanticKernel()` DI extension is implemented.

## Quick start (Story 9.3 target)

```csharp
builder.Services.AddAffiantCore(options => { ... });
builder.Services.AddAffiantSemanticKernel();  // Story 9.3
```

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.SemanticKernel.Filters` | `AffiantFilterPipeline` — registers SK filter pipeline |
| `Affiant.SemanticKernel.Connectors` | Connector capabilities and provider configuration (Story 9.2) |
