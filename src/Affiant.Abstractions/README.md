# Affiant.Abstractions

Domain-agnostic primitives and interfaces for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

This is the root of the framework's dependency graph: zero references to any other Affiant package, zero references to Semantic Kernel / Microsoft Agent Framework / Microsoft.Extensions.AI. A host that only needs to implement a contract — write a custom `IDocketStore`, a custom `IStreamingTransport`, a field mapper — can reference this package alone. Every other Affiant package depends on it, directly or transitively.

## Install

```
dotnet add package Affiant.Abstractions
```

Most hosts get this transitively through `Affiant.Core` or an adapter package (`Affiant.SemanticKernel`, `Affiant.AgentFramework`); reference it directly only when implementing a framework contract without pulling in the rest of the framework.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.Abstractions.Models` | Data types — everything that is not a contract: `Affidavit`, `AffidavitField`, `ProvenanceTag`, `ProvenanceChain`, `ToolEnvelope` (discriminated `ReadResult`/`WriteProposal`/`ToolError`), `DocketEntry`, `ReviewStatus`, `ReviewContext`, `ReviewRequirement`, `ReviewResponse` (discriminated `ReviewGranted`/`ReviewDenied`/`ReviewExpired`), `ChatSession`, `ConversationContext`, `TaskInferenceField`, `Operation`, `EntityRef`, `AffiantChatMessage`, `AffiantToolDescriptor` |
| `Affiant.Abstractions.Interfaces` | Framework contracts a host or adapter implements or consumes — interfaces only: `IChatSessionStore`, `IDocketStore`, `IStreamingTransport`, `IApprovalPolicy`, `IFieldMapper<T>`, `IWriteExecutor`, `IRouteRegistry`, `IIntentInterceptor`, `IToolAuthorizationPolicy`, `ITaskInferenceStrategy` |
| `Affiant.Abstractions.Transport` | Wire-shape types for the streaming transport: `TransportEvent`, `EvidenceCard` request/response, `DocketExpiryEvent`, `SystemNotificationPayload`, `UiGuidancePayload` |
| `Affiant.Abstractions.Attributes` | `[AffiantWriteTool]` — declares a tool method as write-intent for the Tool Descriptor Registry |
| `Affiant.Abstractions.Exceptions` | `AffiantStartupException` — the hard-failure type framework startup validation throws (never a warning) |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the seven normative rules and the layering DAG this package roots
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
