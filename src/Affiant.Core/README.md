# Affiant.Core

Concrete backend-neutral services for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Sits directly above `Affiant.Abstractions` in the framework's dependency graph and is depended on by every adapter package (`Affiant.SemanticKernel`, `Affiant.AgentFramework`, `Affiant.Docket`, `Affiant.EntityFramework`, `Affiant.Policies`, `Affiant.Transport.SignalR`). Core never references an adapter — it consumes adapter-provided implementations of its interfaces through DI.

## Install

```
dotnet add package Affiant.Core
```

```csharp
builder.Services.AddAffiantCore();
```

`AddAffiantCore()` registers the tool descriptor registry, the observability event stream, the scoped `ContextFabric`, and the completion filter pipeline building blocks. It is a prerequisite for every adapter's own `AddAffiant*()` call — see the adapter package (`Affiant.SemanticKernel` or `Affiant.AgentFramework`) for the full host wiring sequence.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.Core.Services` | `ContextFabric` (conversation-scoped entity/field tracking), `ReviewGate` (the review state machine), `ApprovalPolicyEvaluator`, `DenyAllDecisionAuthorization` (the fail-closed default for `IDecisionAuthorizationPolicy`), `DeterministicShortCircuit`, `EvidenceCardRequestFactory`, `SessionLockRegistry` (per-session turn-serialization lock), `AffiantToolRegistry` |
| `Affiant.Core.Filters` | Backend-neutral filter pipeline steps: `ContextExtractor<T>` base class, `TaskInferenceStep`, `ToolErrorFilter`, `ToolTracingFilter`, `ToolArgumentCaptureFilter`, `InferenceTriggerFilter`, `TaskInferenceMergeFilter`, `ReviewGateFilter` |
| `Affiant.Core.Policies` | `ReviewerConfirmationPolicy` — the default `IApprovalPolicy` fallback |
| `Affiant.Core.Observability` | `AffiantTelemetry` (the ActivitySource, the Meter, and the `Record*` emitters for the telemetry-key registry), `DocketDepthInstrument` (the `affiant.docket.pending` gauge), `DeprecatedTelemetryKeys`, `InMemoryObservabilityEventStream<T>` |
| `Affiant.Core.Triggers` | `WriteIntentInferenceTrigger` and the framework's `IInferenceTrigger` implementations |
| `Affiant.Core.UiBridge` | `UiGuidanceBridge` — routes `IRouteRegistry` lookups onto the `TransportEvent.UiGuidance` wire path |
| `Affiant.Core.Extensions` | `ServiceCollectionExtensions` — `AddAffiantCore`, `AddAffiantTool<TStrategy>`, `AddAffiantReadTool`, `AddDeterministicFieldSource<TSource>`, `AddFieldResolver<TResolver>`, `AddAffidavitProjection<TProjection>`, `AddPreviousValueSource<TSource>`, `AddDecisionAuthorization<TPolicy>` |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the seven normative rules and the L2 inference orchestration layer
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
