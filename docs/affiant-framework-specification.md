# Affiant Framework — Specification & Execution Guide

> **Sworn provenance for every AI write.**  
> **Version**: 1.0.0-spec  
> **Last updated**: 2026-04-10  
> **Authors**: Software Architect, Technical Product Manager, Principal Engineer, Technical Writer  
> **Status**: Ready for implementation  
> **Repository**: github.com/affiant-dev/affiant  
> **Packages**: nuget.org/packages/Affiant.*

---

## 1. What This Document Is

This is the canonical specification for the Affiant framework — a deterministic evidence layer for .NET that provides sworn, field-level provenance tracking between LLMs and databases. It serves as both an architectural reference and the `CLAUDE.md` execution guide for the framework repository.

An affiant is one who swears to truth. This framework swears to the provenance of every field an AI proposes to write.

The framework exists because no agent framework today — open-source or enterprise — offers field-level provenance tracking with deterministic context extraction. Enterprise write operations demand the same evidentiary chain that financial transactions require: the user must know *why* the AI suggested each value before approving it. Affiant intercepts every AI-proposed database mutation, tags each field with its deterministic origin, and holds the proposal in a durable review queue — the **Docket** — for human review. Nothing commits without evidence. Nothing writes without approval.

Affiant is built on Semantic Kernel's `IAutoFunctionInvocationFilter` pipeline, which provides the richest interception surface in any .NET agent framework. It cleanly separates into six architectural layers, with the natural boundary between framework and host application falling at three seams: domain plugins, domain models, and transport configuration.

### Ecosystem Vocabulary

The legal/testimonial metaphor that gives Affiant its name also provides a self-consistent vocabulary for every framework concept. This vocabulary is used throughout the specification, the codebase, and developer-facing documentation.

| Framework Concept | Name | Metaphor Source |
|---|---|---|
| The evidence report for a proposed mutation | **Affidavit** | A sworn written statement — exactly what the evidence card is |
| The structured review card shown to humans | **Evidence Card** | Direct functional description |
| A provenance badge on a single field | **Provenance Tag** | Technical precision — badges are visual, tags are data |
| The durable review queue | **Docket** | A court's schedule of cases awaiting review |
| An auto-approval policy for low-risk mutations | **Standing Order** | A court order that applies to recurring situations without individual review |
| An escalation to a senior reviewer | **Referral** | A case referred to a higher authority |
| The human who reviews and approves | **Reviewer** | Deliberately plain |
| A field value overridden during review | **Amendment** | A formal change to a sworn document |
| The complete audit trail for a committed mutation | **Record** | The official record of proceedings |

---

## 2. Primitive Definitions

These are the canonical types. Every implementation must conform to these contracts exactly.

### 2.1 ProvenanceSource (Enum — 7 States)

The provenance taxonomy uses seven states. This is the complete set — do not add states without updating this specification. The ordering below also defines the determinism hierarchy used in confidence-tie merge rules, from most deterministic to least.

```csharp
// Determinism hierarchy: higher in this list wins ties during merge.
// UserStated > External > Computed > Conversation > Inferred > Default > Empty
public enum ProvenanceSource
{
    UserStated,    // User explicitly stated this value (e.g., "my email is john@example.com")
    External,      // Fetched from an authoritative external system (API lookup, database read)
    Computed,      // Derived by deterministic business logic (tax calculation, date math)
    Conversation,  // Mentioned in conversation context but not explicitly stated as a value
    Inferred,      // LLM-inferred from conversation context and tool results
    Default,       // System default or fallback value
    Empty          // Provenance unknown — MUST be explicitly tagged, never omitted
}
```

**Design decision**: `HumanCorrected` is deliberately excluded as a provenance source. When a reviewer amends a field during approval, the amendment is recorded as a new `ProvenanceTag` with source `UserStated`. The original tag is preserved in the `ProvenanceChain` for the Record. This keeps the taxonomy clean: sources describe *where data came from*, not *what happened to it*.

### 2.2 ProvenanceTag (Record)

Every field value in the system carries a `ProvenanceTag`. There are no exceptions to this rule — see Normative Rule 7.

```csharp
public sealed record ProvenanceTag(
    ProvenanceSource Source,      // Which of the 7 sources produced this value
    float Confidence,             // 0.0–1.0 confidence score
    string? Evidence,             // Human-readable explanation of why this source was assigned
    int? ConversationTurn         // Which conversation turn produced this value (null for non-conversational sources)
);
```

### 2.3 ProvenanceChain (Record)

When values are merged or amended across turns, the framework preserves the full history as an ordered chain. This chain is the Record — the audit trail that answers "how did this field arrive at its current value?"

```csharp
public sealed record ProvenanceChain(
    ProvenanceTag Current,                    // The active provenance tag
    IReadOnlyList<ProvenanceTag> Prior        // Previous tags, newest first
);
```

**Merge rule**: When `TaskInferenceStep` produces an inferred value for a field that `ContextFabric` already holds from a higher-confidence source, the higher-confidence value wins. Ties break toward the more deterministic source using the hierarchy defined in `ProvenanceSource`. The losing provenance is appended to the `Prior` list.

### 2.4 ToolEnvelope (Discriminated Union)

`ToolEnvelope` is the single most important type in the framework. It replaces raw `FunctionResult` as the universal exchange type between plugin authors and the context fabric. All tools return one of three variants.

```csharp
// Base record — all tool returns inherit from this
public abstract record ToolEnvelope(string ToolName, DateTimeOffset Timestamp);

// Variant 1: Read operations — returns markdown for dual-audience consumption
public sealed record ReadResult(
    string ToolName,
    DateTimeOffset Timestamp,
    string Summary,                // Human-readable summary for LLM reasoning
    string Markdown,               // Formatted result with [entity:id](link) references
    EntityRef[] Entities            // Structured entity references for ContextExtractor
) : ToolEnvelope(ToolName, Timestamp);

// Variant 2: Write proposals — produces an Affidavit, never executes the write
public sealed record WriteProposal(
    string ToolName,
    DateTimeOffset Timestamp,
    Affidavit Envelope              // The proposed mutation with full provenance (the Affidavit)
) : ToolEnvelope(ToolName, Timestamp);

// Variant 3: Structured errors — plugins must never throw exceptions
public sealed record ToolError(
    string ToolName,
    DateTimeOffset Timestamp,
    string Code,                   // Machine-readable error code (e.g., "CUSTOMER_NOT_FOUND")
    string Message,                // Human-readable error message
    bool Retryable                 // Whether the framework should retry once
) : ToolEnvelope(ToolName, Timestamp);
```

**JSON polymorphism**: Use `[JsonDerivedType]` attributes (matching SK's own `KernelContent` pattern) to enable polymorphic deserialization in the filter pipeline. The `type` discriminator field distinguishes variants during deserialization.

### 2.5 EntityRef (Record)

Entity references extracted from tool results, used by `ContextExtractor` to build the conversation context.

```csharp
public sealed record EntityRef(
    string EntityType,             // e.g., "Customer", "WorkOrder", "Aircraft"
    string EntityId,               // The primary key or unique identifier
    string DisplayName,            // Human-readable label
    Dictionary<string, object> Fields  // Key-value pairs of extracted field data
);
```

### 2.6 Affidavit (Record)

The core write-side contract — formerly called `ConfirmationEnvelope` in the research phase, now named to align with the framework's sworn-testimony metaphor. An Affidavit is the sworn evidence report for a proposed mutation. Every proposed mutation flows through this record, carrying full provenance for every field.

```csharp
public sealed record Affidavit(
    string OperationType,                      // e.g., "UpdateCustomer", "CreateWorkOrder"
    string EntityType,                         // The domain entity being mutated
    string? EntityId,                          // Null for create operations
    AffidavitField[] Fields,                   // Every field with its value and provenance
    float AggregateConfidence,                 // Minimum of all field confidences
    string[] Warnings,                         // Business-rule violations detected
    bool RequiresConfirmation                  // Can be overridden by IApprovalPolicy (Standing Order)
);

public sealed record AffidavitField(
    string Name,                               // Field name (domain-specific)
    object? Value,                             // Proposed value
    object? PreviousValue,                     // Current value (null for creates)
    ProvenanceChain Provenance                 // Full provenance chain for this field
);
```

### 2.7 DocketEntry (Record) — The Review Queue Item

A `DocketEntry` is a pending Affidavit awaiting review. The Docket is the durable review queue.

```csharp
public enum ReviewStatus { Pending, Approved, Rejected, Amended, Expired, Cancelled }

public sealed record DocketEntry(
    Guid EntryId,                              // Idempotency key — prevents double-submit
    string SessionId,
    string TenantId,
    string UserId,
    string? ReviewerUserId,                    // Null = same user; set for Referrals (delegated approval)
    string OperationType,
    Affidavit Envelope,
    ReviewStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,                  // Default TTL: 10 minutes (configurable via Standing Order)
    Dictionary<string, object>? Amendments     // Fields the reviewer changed (Amendments)
);
```

### 2.8 ReviewStep (Record) — Multi-Step Reviews

For operations requiring sequential review steps (Phase 2+). The ReviewGate processes steps sequentially, sending one Evidence Card at a time. This is a state machine, not a workflow engine.

```csharp
public sealed record ReviewStep(
    string StepId,
    string Description,
    AffidavitField[] Fields,
    ReviewStatus Status,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt
);
```

### 2.9 GuidableElement (Record) — UI Bridge

```csharp
public sealed record GuidableElement(
    string ElementId,              // Stable identifier (e.g., "save-button")
    string Selector,               // CSS selector using data-guide attribute
    string Route,                  // Which route/page this element appears on
    string Description,            // Human-readable description for the LLM
    string[] Tags                  // Semantic tags for discovery (e.g., "form", "navigation")
);
```

### 2.10 Event Vocabulary (Enum)

The transport layer uses an explicit enum for all events — never stringly-typed.

```csharp
public enum TransportEvent
{
    AgentTyping,          // Agent is processing (typing indicator)
    AgentChunk,           // Streaming text chunk
    ToolCallStarted,      // A tool invocation has begun
    ToolCallCompleted,    // A tool invocation has completed
    EvidenceCardRequest,  // Evidence Card sent to reviewer
    EvidenceCardResponse, // Reviewer responded to Evidence Card
    UiGuidance,           // UI walkthrough step
    SessionRehydrated,    // Session restored from persistence
    DocketExpiring,       // Warning: a DocketEntry is about to expire
    Error                 // Framework-level error
}
```

---

## 3. Interface Contracts

These are the extension points that host applications and optional adapters implement. The framework depends on these interfaces — never on concrete implementations.

### 3.1 Transport

```csharp
public interface IStreamingTransport
{
    Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct);
    Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct);
    IAsyncEnumerable<TransportMessage> ReceiveAsync(string connectionId, CancellationToken ct);
}
```

### 3.2 Persistence

```csharp
// Modeled after LangGraph's BaseCheckpointSaver pattern
public interface IChatSessionStore
{
    Task<ChatSession> CreateAsync(string tenantId, string userId, CancellationToken ct);
    Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct);
    Task SaveMessagesAsync(string sessionId, IReadOnlyList<ChatMessageContent> messages, CancellationToken ct);
    Task<IReadOnlyList<ChatMessageContent>> LoadMessagesAsync(string sessionId, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);
}

// The Docket — the durable review queue
public interface IDocketStore
{
    Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct);
    Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct);
    Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct);
    Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct);
    Task UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct);
}
```

### 3.3 Approval Policy (Standing Orders and Referrals)

```csharp
// Determines whether an Affidavit requires review, auto-approves (Standing Order), or escalates (Referral)
public interface IApprovalPolicy
{
    Task<ReviewRequirement> EvaluateAsync(Affidavit envelope, ConversationIdentity identity);
}

public enum ReviewRequirement { StandingOrder, ReviewerConfirmation, ReferralRequired, MultiParty }

// The ReviewGate's response types — adopted from Pydantic AI's ToolApproved | ToolDenied pattern
public abstract record ReviewResponse;
public sealed record ReviewGranted(Guid EntryId, Dictionary<string, object>? Amendments) : ReviewResponse;
public sealed record ReviewDenied(Guid EntryId, string? Reason) : ReviewResponse;
public sealed record ReviewExpired(Guid EntryId) : ReviewResponse;
```

### 3.4 Connector Capabilities

```csharp
// Probed at startup — enables automatic fallback for connectors with known limitations
public interface IConnectorCapabilities
{
    bool SupportsAutoFunctionInvocationFilter { get; }
    bool SupportsStreamingFunctionCalls { get; }
    bool SupportsStructuredOutput { get; }
    bool SupportsParallelToolCalls { get; }
}

// Manual function calling fallback for connectors that don't fire IAutoFunctionInvocationFilter
public interface IToolInvocationCapture
{
    Task<FunctionResultContent> CaptureAndInvokeAsync(
        FunctionCallContent functionCall, Kernel kernel, CancellationToken ct);
}
```

### 3.5 Domain Mapping (Host-Implemented)

```csharp
// Host applications implement this to bridge Affidavit fields to domain models
public interface IFieldMapper<TDomainModel>
{
    TDomainModel MapFromAffidavit(Affidavit envelope);
    Affidavit MapToAffidavit(TDomainModel model, string operationType);
}
```

### 3.6 Write Execution (Host-Implemented)

```csharp
// The actual mutation — only called after the ReviewGate receives approval
public interface IWriteExecutor
{
    Task<WriteResult> ExecuteAsync(Affidavit approvedAffidavit, ConversationIdentity identity, CancellationToken ct);
}

public sealed record WriteResult(bool Success, string? EntityId, string? ErrorMessage);
```

### 3.7 UI Guidance

```csharp
public interface IRouteRegistry
{
    void Register(GuidableElement element);
    IReadOnlyList<GuidableElement> GetElementsForRoute(string route);
    IReadOnlyList<GuidableElement> GetAll();
}
```

### 3.8 Intent Interception

```csharp
// For DeterministicShortCircuit — bypasses the LLM entirely for high-failure-cost intents
public interface IIntentInterceptor
{
    int Priority { get; }
    bool CanHandle(string userMessage);
    Task<string> HandleAsync(string userMessage, ConversationIdentity identity, CancellationToken ct);
}
```

### 3.9 Authorization

```csharp
// Modeled after ServiceNow's invoke_from_ai ACL pattern
public interface IToolAuthorizationPolicy
{
    Task<bool> IsAuthorizedAsync(string toolName, ConversationIdentity identity, CancellationToken ct);
}
```

### 3.10 Task Inference Strategy

```csharp
public interface ITaskInferenceStrategy
{
    Task<TaskInferenceOutput> InferAsync(
        string toolName, ConversationContext context, ChatHistory history, CancellationToken ct);
}
```

### 3.11 Tool Descriptor Registry

> Added 2026-05-14 as part of Phase 3 Track A Epic 15 (stories 15.1–15.7). Closes the empty-Affidavit regression identified at commit `b72c1fa` (2026-04-30) and recorded in [`docs/proposals/affiant-validator-handoff.md`](../docs/proposals/affiant-validator-handoff.md).

Every tool the framework orchestrates is described by an `AffiantToolDescriptor` record. The descriptor is the contract that the framework's L2 pipeline (§3.10 Task Inference Strategy) reads when classifying a tool invocation, and the input to the startup validator (§3.11.5 below) that ensures the descriptor registry is exhaustive at every host boot.

**External dependencies this section presumes:**

- `Microsoft.SemanticKernel.Kernel` — the SK kernel whose `Plugins` collection is cross-checked at startup.
- `Microsoft.Extensions.Hosting.IHostedService` — the hosting abstraction the startup validator implements.
- `Microsoft.Extensions.DependencyInjection.IServiceProvider` — used to resolve `InferenceStrategy` types at startup (Check B).

**Glossary for this section:**

- *Affidavit* — the framework's field-level provenance record attached to every write operation. Defined in §2.6 of this document.
- *Empty-Affidavit regression* — the class of bug where a write tool is misclassified as a read tool (or classified without an `InferenceStrategy`), causing the framework to produce Affidavits with all fields at `ProvenanceSource.Empty`. The 2026-04-30 regression at commit `b72c1fa` was the motivating incident.
- *ITaskInferenceStrategy* — the strategy interface (§3.10 above) responsible for inferring structured task context before a write tool executes. Implementations are host-supplied.
- *Startup validator* — `AffiantStartupValidator` in `Affiant.SemanticKernel`; implements `IHostedService` and runs at host boot.

#### 3.11.1 The `Operation` Open Record

`Operation` is an open record (NOT a closed enum), defined as:

```csharp
namespace Affiant.Abstractions.Models;

public sealed record Operation(string Kind)
{
    public static readonly Operation ReadQuery    = new("ReadQuery");
    public static readonly Operation WriteCreate  = new("WriteCreate");
    public static readonly Operation WriteUpdate  = new("WriteUpdate");
    public static readonly Operation WriteDelete  = new("WriteDelete");
}
```

Four well-known static factories ship with the framework. A host needing a fifth kind constructs `new Operation("WriteUpsert")` or `new Operation("MyDomainKind")` without forcing a framework version bump. The framework's filters pattern-match on the four well-known instances; host-defined kinds pass through transparently.

**Why open record, not enum?** A closed enum forces every host to wait for a framework release cadence to introduce a new operation kind. The open-record contract decouples host extensibility from framework release tempo. (Decision D27, documented in [`docs/proposals/affiant-validator-handoff.md`](../docs/proposals/affiant-validator-handoff.md) §10 — D27 reads: "Operation as open record, not enum, to permit host-defined operation kinds without a framework version bump.")

#### 3.11.2 The `AffiantToolDescriptor` Field Set

```csharp
namespace Affiant.Abstractions.Models;

public sealed record AffiantToolDescriptor(
    string FunctionName,
    string? PluginName,
    Operation Operation,
    string? EntityType,
    Type? InferenceStrategy);
```

| Field | Required | Purpose |
|---|---|---|
| `FunctionName` | yes | Matches `KernelFunction.Name`. Together with `PluginName` forms the registry's lookup key. |
| `PluginName` | no | Disambiguates tools with the same name across plugins. `null` matches any plugin (host opt-in unique-name discipline). |
| `Operation` | yes | The operation classification. Framework filters pattern-match on `WriteCreate` / `WriteUpdate` / `WriteDelete` to decide whether pre-tool inference orchestration is required. |
| `EntityType` | no | Domain entity name (host-specific string, e.g. `"WorkOrder"`, `"LeaveRequest"`). Semantically required for `WriteCreate` / `WriteUpdate` operations; `null` for read tools and delete operations. |
| `InferenceStrategy` | no | `Type` implementing `ITaskInferenceStrategy` (§3.10). Semantically required for `WriteCreate` / `WriteUpdate`; `null` for read or delete operations. Resolved from `IServiceProvider` at orchestration time. |

The table's "Required" column reflects record-level type nullability only — it is NOT a semantic contract. The descriptor does not enforce semantic constraints at the C# type level (e.g., "WriteCreate must carry `InferenceStrategy`"). Semantic validation is enforced by the startup validator at runtime (§3.11.5).

#### 3.11.3 The `IAffiantToolRegistry` Contract

```csharp
namespace Affiant.Abstractions.Interfaces;

public interface IAffiantToolRegistry
{
    void Register(AffiantToolDescriptor descriptor);
    AffiantToolDescriptor? Find(string functionName, string? pluginName = null);
    IReadOnlyList<AffiantToolDescriptor> All { get; }
}
```

- `Register` is idempotent on `(FunctionName, PluginName)` — double-registration throws `InvalidOperationException` naming both descriptors.
- `Find` resolves by `(FunctionName, PluginName)` first; if `PluginName` is `null` and multiple plugins expose the same `FunctionName`, `Find` throws rather than picking arbitrarily.
- `All` enumerates every registered descriptor and is used by the startup validator's Check B.

The default implementation (`AffiantToolRegistry` in `Affiant.Core`) is a thread-safe in-memory store. Hosts that prefer an alternate backing store may supply their own implementation via DI, provided it honors the `Register` idempotency and `Find` ambiguity-throws contracts.

#### 3.11.4 The `[AffiantWriteTool]` Attribute

```csharp
namespace Affiant.Abstractions.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AffiantWriteToolAttribute : Attribute
{
    public string Operation         { get; }
    public string EntityType        { get; }
    public Type   InferenceStrategy { get; }

    public AffiantWriteToolAttribute(string operation, string entityType, Type inferenceStrategy)
    {
        Operation         = operation;
        EntityType        = entityType;
        InferenceStrategy = inferenceStrategy;
    }
}
```

**Constructor parameter table:**

| Parameter | Type | Purpose |
|---|---|---|
| `operation` | `string` | The operation kind string (use `Operation.WriteCreate.Kind` etc. for readability, or pass the string literal `"WriteCreate"` directly). |
| `entityType` | `string` | Domain entity name (host-specific, e.g. `"WorkOrder"`). |
| `inferenceStrategy` | `Type` | The `ITaskInferenceStrategy` implementation type, passed as `typeof(TStrategy)`. |

**Usage example:**

```csharp
[KernelFunction, Description("Creates a new work order for the given aircraft.")]
[AffiantWriteTool("WriteCreate", "WorkOrder", typeof(WorkOrderCreateStrategy))]
public async Task<string> CreateWorkOrderAsync(...)
```

`AllowMultiple = false` is enforced — applying the attribute twice to the same method is a compile-time error. The attribute name, namespace, constructor parameter order, and `AllowMultiple` value are part of the public API contract ratified at HIL gate G0 (2026-05-14, [`docs/implementation-artifacts/track-a/g0-descriptor-contract-approval.md`](../../docs/implementation-artifacts/track-a/g0-descriptor-contract-approval.md) Item 4).

#### 3.11.5 Hard Startup-Failure Semantics

The framework's `AffiantStartupValidator` (in `Affiant.SemanticKernel`) implements `IHostedService` and runs at host boot, before any user-traffic-serving code begins. It performs two checks:

**Check A — Registry-vs-kernel cross-check.** For every `KernelFunction` in `Kernel.Plugins`, look up `(function.Name, plugin.Name)` in the registry. If absent, the validator throws `AffiantStartupException` naming every unregistered pair:

> The following `[KernelFunction]` methods are not registered as Affiant tool descriptors:
> - `{pluginName}.{functionName}`
>
> Fix: apply `[AffiantWriteTool(operation, entityType, typeof(TStrategy))]` to the method, or call `services.AddAffiantTool<TStrategy>("FunctionName", Operation.WriteCreate, "EntityType")` during DI setup. For read tools, use `services.AddAffiantReadTool("FunctionName")`.

**Check B — Strategy resolvability.** For every descriptor with non-null `InferenceStrategy`, resolve the type from `IServiceProvider`. If `null` is returned, the validator throws `AffiantStartupException` naming every unresolvable strategy:

> The following Affiant tool descriptors name an inference strategy that cannot be resolved from `IServiceProvider`:
> - `{functionName}` → `{InferenceStrategy.FullName}`
>
> Fix: register the strategy via `services.AddSingleton<TStrategy>()`, or use `AddAffiantTool<TStrategy>(...)` which registers automatically.

**No WARN-and-continue.** Misconfiguration is a hard failure, not a warning log line. There is no `enableValidation: false` switch and there will not be one.

The structural reason: before the validator existed, the framework silently produced empty Affidavits when a write tool was misclassified. An Affidavit with all fields at `ProvenanceSource.Empty` is indistinguishable from a read tool's correct provenance — the error was invisible at runtime and surfaced only in audit reviews. The 2026-04-30 regression at commit `b72c1fa` demonstrated that a warning-and-continue approach does not protect against this class of misconfiguration. The validator is the load-bearing fix. See also: PRD Task 6 preamble in [`docs/architecture/phase-3-prd-a0-tool-descriptor-registry.md`](../../docs/architecture/phase-3-prd-a0-tool-descriptor-registry.md).

Both error-message shapes were ratified as part of the public API contract at HIL gate G0 (2026-05-14, [`docs/implementation-artifacts/track-a/g0-descriptor-contract-approval.md`](../../docs/implementation-artifacts/track-a/g0-descriptor-contract-approval.md) Item 5).

#### 3.11.6 Adopter Integration Paths

A host has exactly two supported paths to register a tool. Both are equivalent and may be mixed within a single host.

**(a) Attribute-driven.** Decorate the `[KernelFunction]` method with `[AffiantWriteTool(operation, entityType, typeof(TStrategy))]`, then call `kernelBuilder.AddAffiantPluginsFromAssembly(typeof(SomeHostType).Assembly, pluginName: "…")`. The walker registers descriptors for every `[KernelFunction]` in the assembly: writes by attribute presence, reads by attribute absence. The strategy type must still be registered separately in DI (e.g., `services.AddSingleton<TStrategy>()`) so Check B passes.

**(b) Explicit DI.** Call `services.AddAffiantTool<TStrategy>("FunctionName", Operation.WriteCreate, "EntityType")` for each write tool, or `services.AddAffiantReadTool("FunctionName")` for each read tool. This path registers both the strategy and the descriptor atomically — no separate `AddSingleton` call required.

The registry's idempotency contract (double-registration throws `InvalidOperationException`) catches accidental overlap when both paths are used in the same host for the same tool.

---

### 3.12 Inference Orchestration & Affidavit Projection

> Added 2026-06-12 as part of Phase 3 Track A Epic 16 (stories 16.1–16.6), ratified 2026-05-05. Addresses the empty-Affidavit regression identified at commit `b72c1fa` (2026-04-30) and recorded in [`docs/proposals/affiant-validator-handoff.md`](../../docs/proposals/affiant-validator-handoff.md).

The L2 inference orchestration layer centralizes two responsibilities that were previously scattered across host implementations: (1) running structured-output inference *before* a write tool executes (pre-tool), so the LLM's intent is captured while the conversation history still reflects the user's unmodified request; and (2) building the resulting `Affidavit` directly from the `ContextFabric` — rather than from per-tool form-data structs that hosts previously had to maintain. The 2026-04-30 regression at commit `b72c1fa` demonstrated why both matters: when inference was decomposed into a post-tool filter, structured-output JSON was parsed from the tool's *return value* where it never existed, and every Affidavit produced was silently fully `ProvenanceSource.Empty`. L2 restores pre-tool inference as a framework concern, preventing the regression class entirely. The architecture was ratified 2026-05-05 (decision D21 — L2 over L1/L3 alternatives; see `docs/proposals/affiant-validator-handoff.md` §10 for the decision rationale).

**Glossary for this section:**

- *Affidavit* — the framework's field-level provenance record attached to every write operation. Each field carries a `ProvenanceChain`. `ProvenanceSource.Empty` marks fields whose origin could not be determined (Rule 7 — see §6).
- *ContextFabric* — the framework's per-session in-memory entity accumulation store. Read tools extract entities into it via `ContextExtractor<TTool>` filters; L2 inference reads from it to build Affidavit fields.
- *`[AffiantWriteTool]`* — the attribute that marks a `[KernelFunction]` as a write-intent tool and associates it with an `InferenceStrategy` type and an `EntityType` string (§3.11.4 above).
- *`ITaskInferenceStrategy`* — the host-supplied strategy interface that declares which fields the framework should infer for a given entity type (§3.10 Task Inference Strategy).
- *`IAffiantToolRegistry`* — the registry that maps `(FunctionName, PluginName)` pairs to `AffiantToolDescriptor` records, including the associated `InferenceStrategy` type (§3.11.3 above).

**External dependencies this section presumes:**

- `Microsoft.SemanticKernel` — the SK kernel's `IFunctionInvocationFilter` and `IAutoFunctionInvocationFilter` pipeline that hosts the L2 filters.
- `System.Diagnostics.ActivitySource` — the .NET OTel instrumentation primitive used by the `Affiant.TaskInference` ActivitySource.
- `Microsoft.Extensions.DependencyInjection` — used to resolve `ITaskInferenceStrategy` implementations from `IServiceProvider` at orchestration time.

#### 3.12.1 The Three New Contracts

L2 introduces three new abstractions in `Affiant.Abstractions.Interfaces`, each with a default implementation in `Affiant.Core` or `Affiant.SemanticKernel`.

**`IInferenceCompletionPort`** is the port through which the framework sends a structured-output inference request to an LLM. Its single method, `CompleteStructuredAsync(InferenceCompletionRequest) → JsonElement`, accepts a request bundle (conversation history, the active `ITaskInferenceStrategy`, the function name, and the current tool arguments) and returns a `JsonElement` whose schema matches the strategy's declared fields. The framework ships one default implementation: `SemanticKernelInferenceCompletionPort` in `Affiant.SemanticKernel`. Hosts that want to route inference through a different LLM provider — or stub it in tests — replace the port via DI without touching any other L2 component.

**`IInferenceTrigger`** decides, per tool invocation, whether inference should run. Its single method, `ShouldRun(InferenceTriggerContext) → bool`, receives the function name, plugin name, current tool arguments, the active `ContextFabric`, and the invocation phase (`PreTool`). The framework registers one default trigger: `WriteIntentInferenceTrigger`, which returns `true` for any tool whose `AffiantToolDescriptor` has `Operation.Kind` equal to `"WriteCreate"` or `"WriteUpdate"`. Hosts may register additional triggers via DI; `InferenceTriggerFilter` short-circuits on the first trigger that returns `true`.

**`IAffidavitProjection`** constructs the Affidavit for a given entity type after inference results are merged into the `ContextFabric`. Its `Project(IContextFabric, operationType, warnings) → Affidavit` method reads fields from the fabric, applies `IDeterministicFieldSource` overrides (see below), and falls back to `ProvenanceTag.Empty` for any field the fabric cannot satisfy (Rule 7). The default implementation is `SchemaDrivenAffidavitProjection` in `Affiant.Core`.

**`IDeterministicFieldSource`** is an augmentation surface for fields that should always come from a deterministic source (e.g., a system clock, a session-authenticated user ID) rather than from LLM inference. `SchemaDrivenAffidavitProjection` checks registered `IDeterministicFieldSource` implementations per field before consulting the fabric; the first non-null resolution wins.

*Source files:* `packages/src/Affiant.Abstractions/Interfaces/IInferenceCompletionPort.cs`, `packages/src/Affiant.Abstractions/Interfaces/IInferenceTrigger.cs`, `packages/src/Affiant.Abstractions/Interfaces/IAffidavitProjection.cs`, `packages/src/Affiant.Abstractions/Interfaces/IDeterministicFieldSource.cs`

#### 3.12.2 Default Services

Three default service implementations ship with the framework. Hosts that accept the defaults need only call `AddAffiantInferenceOrchestration()` (§3.12.3) during DI setup.

**`TaskInferenceRunner`** (in `Affiant.Core.Services`) is the stateless orchestrator that bridges `IInferenceCompletionPort` and the merge step. It builds an `InferenceCompletionRequest`, calls the port, forwards the resulting `JsonElement` to `TaskInferenceStep` for confidence-based merge into the `ContextFabric`, and emits the `inference.completed` span event. On any non-cancellation exception it emits `inference.failed`, logs a warning at `LogWarning` level, and returns an empty `TaskInferenceResult` — the fail-safe contract (§3.12.7).

**`WriteIntentInferenceTrigger`** (in `Affiant.Core.Triggers`) is the default `IInferenceTrigger` registered by `AddAffiantInferenceOrchestration()`. It fires inference for any tool whose registered `AffiantToolDescriptor` has `Operation.Kind` of `"WriteCreate"` or `"WriteUpdate"`.

**`SchemaDrivenAffidavitProjection`** (in `Affiant.Core.Services`) is the default `IAffidavitProjection`. It iterates the fields declared by the active `ITaskInferenceStrategy`, applies `IDeterministicFieldSource` overrides first, then reads from the `ContextFabric`, and falls back to `ProvenanceTag.Empty` for any unresolved field (Rule 7). After projection it emits the `affidavit.projected` span event and publishes a typed `AffidavitEmittedEvent` through `IObservabilityEventStream<AffidavitEmittedEvent>` for downstream subscribers.

**`FunctionNameInferenceTrigger`** (in `Affiant.Core.Triggers`) is a soft-deprecated `IInferenceTrigger` that fires by explicit function-name allowlist rather than registry classification. It exists to support hosts adopted before the Tool Descriptor Registry (§3.11) was available, carries an `[Obsolete]` attribute, and will be removed before v1.0.0. Hosts should migrate to `WriteIntentInferenceTrigger` with `[AffiantWriteTool]` decoration.

*Source files:* `packages/src/Affiant.Core/Services/TaskInferenceRunner.cs`, `packages/src/Affiant.Core/Triggers/WriteIntentInferenceTrigger.cs`, `packages/src/Affiant.Core/Services/SchemaDrivenAffidavitProjection.cs`, `packages/src/Affiant.Core/Triggers/FunctionNameInferenceTrigger.cs`

#### 3.12.3 SK Adapter Surface

The L2 SK-specific components live in `Affiant.SemanticKernel`, keeping the `Microsoft.SemanticKernel` PackageReference isolated to that package. `Affiant.Core` reaches SK behaviour only through `Affiant.Abstractions`'s interface contracts — this is the L2 AC #4 architectural constraint ratified 2026-05-05: Core must not take a direct SK dependency.

**`SemanticKernelInferenceCompletionPort`** (in `Affiant.SemanticKernel.Adapters`) implements `IInferenceCompletionPort` using SK's `IChatCompletionService` with structured-output mode. It converts the `InferenceCompletionRequest` into a `ChatHistory` with a system prompt derived from the strategy's field schema, calls the completion service, and parses the response into a `JsonElement`.

**`InferenceTriggerFilter`** (in `Affiant.SemanticKernel.Filters`) implements SK's `IFunctionInvocationFilter` (pre-tool). It iterates registered `IInferenceTrigger` instances, enforces once-per-`(ConversationId, FunctionName, TurnNumber)` idempotency via `ContextFabric` bookkeeping, resolves the `ITaskInferenceStrategy` from `IAffiantToolRegistry` and `IServiceProvider`, and delegates to `TaskInferenceRunner`. Inference failure is absorbed (fail-safe per §3.12.7); the tool call always proceeds.

**`ToolArgumentCaptureFilter`** (in `Affiant.SemanticKernel.Filters`) implements `IFunctionInvocationFilter` (pre-tool). It runs before `InferenceTriggerFilter` in the pipeline order (§3.12.4) and captures tool arguments into the `ContextFabric` so that `IAffidavitProjection` implementations can incorporate argument-sourced provenance.

**`AddAffiantInferenceOrchestration()`** is the `IServiceCollection` extension method in `Affiant.SemanticKernel.Extensions.ServiceCollectionExtensions` that registers all L2 components with a single call: `SemanticKernelInferenceCompletionPort`, `TaskInferenceRunner`, `WriteIntentInferenceTrigger`, `InferenceTriggerFilter`, `ToolArgumentCaptureFilter`, `SchemaDrivenAffidavitProjection` (keyed per registered tool), and the SK-adapter startup validator extension.

*Source files:* `packages/src/Affiant.SemanticKernel/Adapters/SemanticKernelInferenceCompletionPort.cs`, `packages/src/Affiant.SemanticKernel/Filters/InferenceTriggerFilter.cs`, `packages/src/Affiant.SemanticKernel/Filters/ToolArgumentCaptureFilter.cs`, `packages/src/Affiant.SemanticKernel/Extensions/ServiceCollectionExtensions.cs`

#### 3.12.4 Pipeline Order

The canonical 7-step filter pipeline order — locked by `AffiantFilterPipelineOrderTests` (`packages/tests/Affiant.SemanticKernel.Tests/Filters/AffiantFilterPipelineOrderTests.cs`, landed in Story 16.4) — is:

```
Pre-tool (IFunctionInvocationFilter):
  1. ToolErrorFilter
  2. DeterministicShortCircuit
  3. ContextExtractor* (host-registered)
  4. ToolArgumentCaptureFilter
  5. InferenceTriggerFilter
[tool invokes]
Post-tool (IAutoFunctionInvocationFilter):
  6. TaskInferenceMergeFilter
  7. ReviewGateFilter
```

Steps 4 and 5 are the L2 additions. `ToolArgumentCaptureFilter` (step 4) must precede `InferenceTriggerFilter` (step 5) so that captured arguments are available to `ITaskInferenceStrategy` implementations during inference. `TaskInferenceMergeFilter` (step 6) is a post-tool `IAutoFunctionInvocationFilter` that merges deferred inference results from the `ContextFabric` into the final Affidavit via `IAffidavitProjection`; it runs before `ReviewGateFilter` (step 7) so the Affidavit is fully populated before the reviewer sees it. Steps 1–3 and 7 were part of the L1 pipeline; see §3.10 Task Inference Strategy and §7 Tool Authoring Guide for their documentation.

*Source files:* `packages/src/Affiant.SemanticKernel/Filters/AffiantFilterPipeline.cs`, `packages/src/Affiant.SemanticKernel/Filters/InferenceTriggerFilter.cs`, `packages/src/Affiant.SemanticKernel/Filters/ToolArgumentCaptureFilter.cs`, `packages/src/Affiant.Core/Filters/TaskInferenceMergeFilter.cs`

#### 3.12.5 Observability Contract

The framework emits L2 inference telemetry through the `Affiant.TaskInference` ActivitySource (registered as `AffiantTelemetry.AffiantTaskInferenceActivitySource` in `Affiant.Core.Observability`), separately from the main `Affiant.Framework` ActivitySource. Consumers that want only inference events can subscribe to `Affiant.TaskInference` alone; the Phase 3.5 Validator service will subscribe to this source to monitor inference quality without being coupled to the full-pipeline trace.

**Five span events** are emitted as `ActivityEvent` instances to the current OTel Activity:

| Event | Emitter | Attribute keys |
|---|---|---|
| `inference.triggered` | `InferenceTriggerFilter` | `affiant.function.name`, `affiant.plugin.name`, `affiant.entity.type`, `affiant.strategy.type` |
| `inference.skipped` | `InferenceTriggerFilter` | `affiant.function.name`, `affiant.skip.reason` (`"not_a_write_tool"` or `"no_strategy_registered"`) |
| `inference.completed` | `TaskInferenceRunner` | `affiant.fields.merged`, `affiant.fields.in_response`, `affiant.fields.in_schema` |
| `inference.failed` | `TaskInferenceRunner` | `affiant.function.name`, `affiant.error.kind` (`"cancelled"`, `"json_parse"`, or `"provider_outage"`) |
| `affidavit.projected` | `SchemaDrivenAffidavitProjection` | `affiant.affidavit.populated_field_count`, `affiant.affidavit.aggregate_confidence`, `affiant.affidavit.empty_provenance_field_count` |

All 12 attribute key strings are constants in `Affiant.Core.Observability.L2TelemetryKeys`. They are part of the public observability API at v1.0.0 — renaming or removing any key requires a v2.0.0 major-version bump.

**Typed event publication.** After projection, `SchemaDrivenAffidavitProjection` publishes a typed `AffidavitEmittedEvent` record through `IObservabilityEventStream<AffidavitEmittedEvent>`. The event carries `ConversationId`, `AffidavitId`, `OperationType`, `EntityType`, `PopulatedFieldCount`, `AggregateConfidence`, and `EmptyProvenanceFieldCount`. The Phase 3.5 Validator subscribes to this stream to perform quality audits without coupling to OTel infrastructure; hosts that want dashboard-level monitoring subscribe to the OTel span events instead.

*Source files:* `packages/src/Affiant.Core/Observability/AffiantTelemetry.cs`, `packages/src/Affiant.Abstractions/Models/AffidavitEmittedEvent.cs`, `packages/src/Affiant.Core/Services/TaskInferenceRunner.cs`, `packages/src/Affiant.Core/Services/SchemaDrivenAffidavitProjection.cs`

#### 3.12.6 Adopter Pattern

A host enabling L2 inference on a write tool needs three things:

1. **Decorate the `[KernelFunction]` method** with `[AffiantWriteTool]` (§3.11.4), specifying the operation kind, entity type, and strategy type — e.g., `[AffiantWriteTool("WriteCreate", "WorkOrder", typeof(WorkOrderCreateStrategy))]`.
2. **Implement `ITaskInferenceStrategy`** (§3.10 Task Inference Strategy). The strategy's `Fields` list declares which entity fields the framework should infer and their confidence thresholds. A typical 5-field entity strategy is approximately 30 lines.
3. **Call `AddAffiantInferenceOrchestration()`** once in DI setup. The extension registers all L2 components.

Optionally, a host may register `IDeterministicFieldSource` implementations for fields that have authoritative non-LLM sources (e.g., the authenticated user ID). These are registered with `services.AddSingleton<IDeterministicFieldSource, YourFieldSource>()` and are automatically picked up by `SchemaDrivenAffidavitProjection`.

The contrast with the pre-L2 pattern is significant: before L2, each write tool required approximately 350 lines across a per-tool inference filter, a per-tool form-data struct, a per-tool Affidavit mapper, and per-field provenance assignments — all host-maintained and all outside the framework's guarantees. With L2, the same coverage requires the `[AffiantWriteTool]` decoration, the strategy declaration (~30 lines), and the one-line DI registration.

*Source files:* `packages/src/Affiant.SemanticKernel/Extensions/ServiceCollectionExtensions.cs`, `packages/src/Affiant.Abstractions/Attributes/AffiantWriteToolAttribute.cs`

#### 3.12.7 Fail-Safe Semantics

Inference failure never breaks the agent turn. The fail-safe contract, enforced jointly by `InferenceTriggerFilter` and `TaskInferenceRunner`, is: any `Exception` other than `OperationCanceledException` thrown during inference is caught, an `inference.failed` span event is emitted with `affiant.error.kind` populated, a warning is logged at `LogWarning` level, and the tool call proceeds via `next(context)`. The agent receives a tool return and produces a non-null response.

`OperationCanceledException` is deliberately re-thrown — cancellation is user- or host-initiated and must propagate normally.

The fail-safe contract is asserted end-to-end by `InferenceFailSafeIntegrationTests` (`packages/tests/Affiant.SemanticKernel.Tests/Integration/InferenceFailSafeIntegrationTests.cs`, landed in Story 16.6).

*Source files:* `packages/src/Affiant.SemanticKernel/Filters/InferenceTriggerFilter.cs`, `packages/src/Affiant.Core/Services/TaskInferenceRunner.cs`

#### 3.12.8 Idempotency Semantics

Inference runs at most once per `(ConversationId, FunctionName, TurnNumber)` tuple within a single agent turn, even when multiple `IInferenceTrigger` instances are registered and more than one returns `true`. Idempotency is enforced by `InferenceTriggerFilter` via a bookkeeping entity maintained in the `ContextFabric` under the reserved key `"inference_idempotency"`. When the filter evaluates a tool call and finds the tuple already marked, it skips inference and proceeds directly to `next(context)`.

`ConversationId` is read from `kernel.Data["ConversationId"]`; `TurnNumber` from `kernel.Data["AffiantTurnNumber"]`. If either is absent the filter falls back to a stable per-fabric-instance hash (`ConversationId`) or zero (`TurnNumber`) — a conservative fallback that may coalesce tuples across conversations on the same fabric instance, but never double-infers within a single conversation turn.

The idempotency contract is asserted end-to-end by `InferenceIdempotencyIntegrationTests` (`packages/tests/Affiant.SemanticKernel.Tests/Integration/InferenceIdempotencyIntegrationTests.cs`, landed in Story 16.6).

*Source files:* `packages/src/Affiant.SemanticKernel/Filters/InferenceTriggerFilter.cs`

---

## 4. Six-Layer Dependency Graph

The architecture separates into six layers forming a directed acyclic graph, with one intentional async cycle between the `ReviewGate` and the transport layer (mediated by event passing, not synchronous dependency — mirroring the Durable Functions `WaitForExternalEvent` pattern).

### Layer 1: Transport

**Components**: `StreamingChatTransport` (implements `IStreamingTransport`), `TransportEvent` enum.

**Responsibility**: Bidirectional communication between the agent and the frontend. Abstracts the wire protocol so the framework doesn't depend on SignalR directly.

**Depends on**: Nothing (bottom of the stack).

**Implemented by**: `Affiant.Transport.SignalR` adapter (reference implementation).

### Layer 2: Orchestration

**Components**: `ProviderSwap` (wraps SK's `IChatCompletionService` configuration), `Kernel Runtime` (SK's `FunctionChoiceBehavior`), `DeterministicShortCircuit` (implements `IIntentInterceptor` pipeline).

**Responsibility**: Routes user messages to either the LLM (for open-ended reasoning) or deterministic handlers (for high-failure-cost intents like delete/cancel). Manages provider selection and fallback.

**Depends on**: Transport (Layer 1) for streaming responses.

### Layer 3: Context Fabric

**Components**: `ContextFabric` (stateful container per conversation), `ContextExtractor<TTool>` (post-invocation filter for read tools), `TaskInferenceStep` (pre-invocation filter for write tools), `IDocketStore` (for context persistence).

**Responsibility**: Builds and maintains structured, auditable conversation context without relying on the LLM to be truthful about data origins. This is the framework's genuinely novel contribution — no other framework offers this.

**Depends on**: Orchestration (Layer 2) for the `IChatCompletionService` used by `TaskInferenceStep`.

**Fragile dependency**: `TaskInferenceStep` depends on both `IDocketStore` and `IChatCompletionService`. If the inference call fails, the mitigation is a fallback path: produce an Affidavit using only deterministic context, with lower confidence scores and a warning that inference was unavailable.

### Layer 4: Review Gate (Approval)

**Components**: `ReviewGate`, `Affidavit`, `ProvenanceTag`, `DocketEntry`.

**Responsibility**: Gates all write operations behind human review. Files Affidavits into the Docket, manages TTL expiry, routes to the appropriate review requirement level based on `IApprovalPolicy` (Standing Orders for auto-approval, Referrals for escalation).

**Depends on**: Transport (Layer 1) for sending Evidence Card requests and receiving responses. Context Fabric (Layer 3) for the provenance data on each field.

**Intentional async cycle**: `ReviewGate` sends `EvidenceCardRequest` events *down* through the transport and receives `EvidenceCardResponse` events *up* through the transport.

### Layer 5: Plugin Runtime

**Components**: `ToolEnvelope<TResult>` discriminated union, read contract (`ReadResult`), write contract (`WriteProposal`), error contract (`ToolError`).

**Responsibility**: Defines the universal exchange type that all plugins conform to. Plugin authors interact only with this layer — they never touch the context fabric or ReviewGate directly.

**Depends on**: Nothing in the framework (plugins are host-implemented). The framework's filters in Layers 3 and 4 depend *on* this layer's types.

### Layer 6: UI Bridge

**Components**: `UiGuidanceBridge`, `IRouteRegistry`, `GuidableElement`.

**Responsibility**: Translates LLM guidance intents into `data-guide` selector payloads sent via the transport. The LLM discovers guidable elements through the registry (injected into the system prompt), not by inspecting the DOM.

**Depends on**: Transport (Layer 1) for emitting `UiGuidance` events.

### Package Mapping

The NuGet package hierarchy reflects the reserved `Affiant.*` namespace on nuget.org:

**`Affiant.Abstractions`** — Bottom of the framework dependency graph: all domain-agnostic primitive types (`ToolEnvelope`, `Affidavit`, `ProvenanceTag`, `ProvenanceChain`, `DocketEntry`, `TransportEvent`, etc.) and all framework interfaces (`IChatSessionStore`, `IDocketStore`, `IStreamingTransport`, `IApprovalPolicy`, `IFieldMapper<T>`, `IWriteExecutor`, `IRouteRegistry`, `IIntentInterceptor`, etc.). Zero dependencies on other Affiant packages. Host applications that only need to implement a contract can reference this package alone without pulling in `Core`'s services.

**`Affiant.Core`** — Concrete services built on top of `Affiant.Abstractions`: the `ContextFabric`, `ReviewGate`, `ContextExtractor<T>` base class, `TaskInferenceStep`, `DeterministicShortCircuit`, `UiGuidanceBridge`, `AffiantTelemetry`, and the DI registration extension `AddAffiantCore()`. References `Affiant.Abstractions`. This layering matches the standard `Microsoft.Extensions.*.Abstractions` / `Microsoft.Extensions.*` convention.

**`Affiant.SemanticKernel`** — The `IAutoFunctionInvocationFilter` implementations that wire Affiant into SK's filter pipeline. The connector-quirk abstraction (`IConnectorCapabilities`, `ManualToolInvoker`), and the provider configuration.

**`Affiant.Docket`** — The durable review queue with persistence. Contains `IDocketStore` implementations and the `DocketExpiryService` background worker. Persistence adapters: PostgreSQL (via EF Core with `jsonb`), SQLite (for development), InMemory (for tests).

**`Affiant.EntityFramework`** — EF Core interceptor for the propose-review-commit pattern. The `IChatSessionStore` implementations, migration helpers, and the row-per-message schema.

**`Affiant.Policies`** — Standing Orders (auto-approval rules), Referral logic (escalation), risk-scoring functions, and the `IApprovalPolicy` evaluation pipeline.

**`Affiant.Transport.SignalR`** — SignalR adapter implementing `IStreamingTransport`. Reference transport for real-time Evidence Card delivery.

**Host** (application-specific, never in a package): Domain plugins, domain models, `IFieldMapper<T>` implementations, SignalR hub hosting, frontend application, authentication, authorization policies, system prompt content.

**Future packages** (Phase 3): `@affiant/react` (npm — React hook `useGuidance` + Evidence Card component), `Affiant.UiBridge.Blazor`.

---

## 5. Framework Boundary Contract

The boundary between framework and host is a three-seam cut. Everything below the seams is reusable. Everything above is host-specific.

**Seam 1 — Domain Plugins**: Host applications implement `[KernelFunction]`-decorated methods that return `ReadResult` or `WriteProposal` via the `ToolEnvelope` contract. The framework never knows about aviation work orders, HR leave requests, or CRM contacts. It only knows about `ToolEnvelope<TResult>`.

**Seam 2 — Domain Models**: The framework's `Affidavit.Fields[]` carries `string` field names and `object` values. The host application's strongly-typed domain models live outside the framework. The `IFieldMapper<TDomainModel>` interface converts between the two.

**Seam 3 — Transport Configuration**: The SignalR hub, authentication middleware, and frontend application are host concerns. The framework provides `IStreamingTransport` and the reference `Affiant.Transport.SignalR` adapter. The host configures and hosts it.

---

## 6. Seven Normative Rules

These rules are non-negotiable. Every implementation, code review, and plugin authoring decision must conform to them. Each rule includes its rationale and a concrete anti-pattern that violates it.

**Rule 1: One system prompt per agent, immutable after initialization.** The system prompt establishes the agent's persona, behavioral constraints, and tool-calling conventions. It must never be modified at runtime by filters or tools. *Rationale*: Mutable system prompts create untraceable behavioral changes that undermine provenance guarantees. *Anti-pattern*: Appending context to the system prompt instead of using the `ContextFabric`.

**Rule 2: Dual-audience tool returns.** Every tool return must be readable by both the LLM (for reasoning) and the UI (for rendering). Read tools return markdown tables with embedded `[entity:id](link)` references. Write tools return an Affidavit that the LLM summarizes while the UI renders as an Evidence Card. *Rationale*: Single-audience returns force re-querying or lossy summarization. *Anti-pattern*: Returning raw SQL result sets or opaque JSON that the LLM must interpret without structure.

**Rule 3: Write tools never write.** Write-intent tools produce `WriteProposal` envelopes containing the *proposed* Affidavit with full provenance. The actual write happens only after the `ReviewGate` receives reviewer confirmation and invokes the host's `IWriteExecutor`. *Rationale*: This is the entire point of the framework — deterministic, auditable, reversible-before-commit mutations. *Anti-pattern*: A "write" tool that calls `dbContext.SaveChanges()` inside the `[KernelFunction]` method.

**Rule 4: Filters over prompts for determinism.** Context extraction, task inference, and review gating happen in SK filters, not in prompt engineering. Prompts request tool calls; filters process the results deterministically. *Rationale*: Prompt-based context extraction is non-deterministic, non-auditable, and varies by model. Filter-based extraction produces identical results regardless of which LLM provider is active. *Anti-pattern*: Adding "After calling the tool, extract the customer's email from the result" to the system prompt.

**L2 example (Story 16.3, 2026-05-16):** The empty-Affidavit regression of 2026-04-30 (commit `b72c1fa`) decomposed Meridian's host-side pre-tool inference filter into a generic post-tool framework filter that ran after every auto-invoked tool. The decomposition was behaviorally lossy: structured-output JSON from the LLM's *intent* (pre-tool) ended up parsed from the tool's *return value* (post-tool), where it never existed. The L2 fix (Story 16.3) restored pre-tool inference as a framework filter — `InferenceTriggerFilter` — which decides per-tool whether to run inference and forwards through `TaskInferenceRunner` to a host-specified `ITaskInferenceStrategy`. The fix is faithful to Rule 4: pre-tool decision logic stays in a filter, never in a prompt. Hosts cannot "ask the LLM to fill in fields" by string concatenation; they declare a strategy and the framework's filter handles the rest. See §3.12 Inference Orchestration & Affidavit Projection for the full surface.

**Rule 5: Graceful degradation on provider failure.** When the primary LLM provider fails, the framework falls back to a secondary provider or enters degraded mode where only deterministic operations (read tools, keyword-matched intents) are available. *Rationale*: Enterprise applications cannot show users a blank screen when an LLM API has an outage. *Anti-pattern*: Throwing an unhandled exception when `IChatCompletionService.GetChatMessageContentsAsync` fails.

**Rule 6: `data-guide` contracts are UI-layer registrations, not LLM-layer concerns.** The agent discovers guidable UI elements through the `IRouteRegistry`, not by inspecting the DOM or asking the user. *Rationale*: LLMs cannot reliably generate CSS selectors, and DOM structures change between deployments. *Anti-pattern*: Prompting the LLM to "find the button labeled Save" by generating a querySelector.

**Rule 7: Every Affidavit field carries provenance, no exceptions.** If a field's provenance is unknown, it must be tagged `ProvenanceSource.Empty` — never omitted. The Evidence Card renders provenance as visual indicators (green for UserStated, amber for Inferred, grey for Default). *Rationale*: Missing provenance is indistinguishable from "the framework forgot to track it" versus "the AI made this up." *Anti-pattern*: Fields without provenance tags that the UI renders identically to user-confirmed values.

---

## 7. Tool Authoring Guide

This section enables a developer unfamiliar with the framework to write a working read/write plugin pair.

### 7.1 Method Signatures

All `[KernelFunction]` methods return `Task<string>` (SK requirement — the string is the serialized `ToolEnvelope`). Parameters use `[Description]` attributes with user-intent language, not implementation language.

```csharp
// GOOD: User-intent language in descriptions
[KernelFunction, Description("Search customers by name or email address")]
public async Task<string> SearchCustomers(
    [Description("The customer's name or email address to search for")] string query)

// BAD: Implementation language
[KernelFunction, Description("Query customer table")]
public async Task<string> SearchCustomers(
    [Description("String search param")] string q)
```

### 7.2 Read Tool Pattern

Read tools return `ReadResult` with markdown formatted for dual-audience consumption and structured entity references for the `ContextExtractor`.

```csharp
public class CustomerPlugin(IServiceScopeFactory scopeFactory)
{
    [KernelFunction, Description("Search customers by name or email")]
    public async Task<string> SearchCustomers(
        [Description("Search term — the customer's name or email")] string query)
    {
        // Plugins are singletons — create a scope for DbContext
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customers = await db.Customers
            .Where(c => c.Name.Contains(query) || c.Email.Contains(query))
            .Take(10)
            .ToListAsync();

        // Build dual-audience result: markdown for LLM + entities for ContextExtractor
        var markdown = "| Name | Email | Phone |\n|---|---|---|\n" +
            string.Join("\n", customers.Select(c =>
                $"| [customer:{c.Id}]({c.Name}) | {c.Email} | {c.Phone} |"));

        var entities = customers.Select(c => new EntityRef(
            EntityType: "Customer",
            EntityId: c.Id.ToString(),
            DisplayName: c.Name,
            Fields: new Dictionary<string, object>
            {
                ["email"] = c.Email,
                ["phone"] = c.Phone ?? "",
                ["name"] = c.Name
            })).ToArray();

        var result = new ReadResult(
            ToolName: "SearchCustomers",
            Timestamp: DateTimeOffset.UtcNow,
            Summary: $"Found {customers.Count} customers matching '{query}'",
            Markdown: markdown,
            Entities: entities);

        return JsonSerializer.Serialize(result);
    }
}
```

### 7.3 Write Tool Pattern

Write tools return `WriteProposal` containing an Affidavit — they NEVER execute the mutation. The `ReviewGate` handles confirmation, and `IWriteExecutor` handles execution.

```csharp
[KernelFunction, Description("Update a customer's contact information")]
public async Task<string> UpdateCustomer(
    [Description("The customer ID to update")] string customerId,
    [Description("The new email address, if changing")] string? email = null,
    [Description("The new phone number, if changing")] string? phone = null)
{
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var customer = await db.Customers.FindAsync(int.Parse(customerId));

    if (customer is null)
        return JsonSerializer.Serialize(new ToolError(
            "UpdateCustomer", DateTimeOffset.UtcNow, "CUSTOMER_NOT_FOUND",
            $"No customer found with ID {customerId}", Retryable: false));

    // Build Affidavit fields with provenance
    var fields = new List<AffidavitField>();

    if (email is not null)
        fields.Add(new AffidavitField("email", email, customer.Email,
            new ProvenanceChain(
                new ProvenanceTag(ProvenanceSource.UserStated, 0.95f, "Parameter from tool call", null),
                Array.Empty<ProvenanceTag>())));

    if (phone is not null)
        fields.Add(new AffidavitField("phone", phone, customer.Phone,
            new ProvenanceChain(
                new ProvenanceTag(ProvenanceSource.UserStated, 0.90f, "Parameter from tool call", null),
                Array.Empty<ProvenanceTag>())));

    var affidavit = new Affidavit(
        OperationType: "UpdateCustomer",
        EntityType: "Customer",
        EntityId: customerId,
        Fields: fields.ToArray(),
        AggregateConfidence: fields.Min(f => f.Provenance.Current.Confidence),
        Warnings: Array.Empty<string>(),
        RequiresConfirmation: true);

    return JsonSerializer.Serialize(new WriteProposal(
        "UpdateCustomer", DateTimeOffset.UtcNow, affidavit));
}
```

### 7.4 ContextExtractor Registration

Each read tool with entity-rich results should have a companion `ContextExtractor<TTool>`. The extractor fires as a post-invocation `IAutoFunctionInvocationFilter`, pattern-matches on the tool name, and writes extracted entities into the `ContextFabric`.

```csharp
public class CustomerSearchExtractor : IAutoFunctionInvocationFilter
{
    private readonly ContextFabric _fabric;

    public CustomerSearchExtractor(ContextFabric fabric) => _fabric = fabric;

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        await next(context);  // Let the tool execute first

        if (context.Function.Name != "SearchCustomers") return;

        var result = JsonSerializer.Deserialize<ReadResult>(
            context.Result.GetValue<string>()!);

        // Write entities into the context fabric — no LLM call involved
        foreach (var entity in result!.Entities)
            _fabric.AddEntity(context.SessionId, entity);
    }
}

// Registration at startup:
// services.AddSingleton<IAutoFunctionInvocationFilter, CustomerSearchExtractor>();
```

### 7.5 Error Handling

Plugins must never throw exceptions that propagate to the LLM. Catch all exceptions and return a `ToolError` envelope.

```csharp
// GOOD: Structured error with retryability hint
catch (DbUpdateException ex)
{
    return JsonSerializer.Serialize(new ToolError(
        "UpdateCustomer", DateTimeOffset.UtcNow,
        Code: "DB_UPDATE_FAILED",
        Message: "Unable to update the customer record. Please try again.",
        Retryable: true));
}

// BAD: Throwing an exception that reaches the LLM as an error string
catch (Exception ex)
{
    throw; // The LLM sees a stack trace and hallucinates a recovery strategy
}
```

### 7.6 DI Lifetime

Plugins are singletons (SK's `KernelPluginFactory.CreateFromType<T>()` creates a single instance). For scoped dependencies like `DbContext` or `HttpClient`, inject `IServiceScopeFactory` and create a scope per invocation, as shown in the examples above.

---

## 8. Observability Contract

The framework emits telemetry via OpenTelemetry using the GenAI semantic conventions (semconv v1.40.0+) with a custom `affiant.*` attribute namespace.

### ActivitySource and Meter Registration

```csharp
private static readonly ActivitySource ActivitySource = new("Affiant.Framework");
private static readonly Meter Meter = new("Affiant.Framework");
```

### Per-Turn Trace Schema

Every agent turn produces a root span with child spans for each LLM call, tool execution, and review interaction. Provenance data is emitted as span events.

```
[Root] invoke_agent "CopilotAgent"
│   gen_ai.conversation.id: "conv_abc123"
│   affiant.turn.number: 3
│   affiant.user.intent: "update_record"
│
├── [Child] chat gpt-4o
│       gen_ai.usage.input_tokens: 1250
│       gen_ai.usage.output_tokens: 340
│       affiant.llm.purpose: "orchestration"
│
├── [Child] execute_tool "SearchCustomers"
│       gen_ai.tool.name: "SearchCustomers"
│       affiant.context.delta: {added: ["customer.email"], removed: []}
│
├── [Event] affiant.provenance
│       affiant.provenance.fields: {
│           "email": {source: "UserStated", confidence: 0.99},
│           "name": {source: "External", confidence: 0.95}
│       }
│
├── [Event] affiant.review
│       affiant.review.outcome: "approved"
│       affiant.review.human_latency_ms: 4500
│
└── [Summary on root]
    affiant.turn.total_tokens: 1590
    affiant.turn.total_cost_usd: 0.0045
    affiant.turn.tool_count: 2
```

### Key Metrics

The framework emits four core metrics: `affiant.turn.duration` (histogram), `affiant.review.wait_duration` (histogram), `affiant.token.usage` (counter by purpose — orchestration vs inference), and `affiant.review.outcome` (counter by result — approved/rejected/expired/standing_order).

---

## 9. Persistence Schema

Messages are stored as rows, not serialized blobs, enabling queries like "find all Docket entries for tenant X in the last 24 hours."

```
ChatSessions:
    SessionId (PK), TenantId, UserId, CreatedAt, LastActivityAt

ChatMessages:
    MessageId (PK), SessionId (FK), Ordinal, Role, Content, AuthorName,
    ModelId, ToolCallId, FunctionName, Arguments (jsonb), Metadata (jsonb), Timestamp

Docket:
    EntryId (PK, GUID), SessionId (FK), TenantId, UserId, ReviewerUserId,
    OperationType, Affidavit (jsonb), ProvenanceChains (jsonb),
    CreatedAt, ExpiresAt, Status (Pending|Approved|Rejected|Amended|Expired|Cancelled)

ConversationContext:
    SessionId (PK, FK), Entities (jsonb), FieldValues (jsonb),
    ProvenanceChains (jsonb), LastUpdatedAt
```

**Session rehydration sequence**: Load messages → reconstruct `ChatHistory` → load `ConversationContext` → load any Docket entries with status `Pending` → apply `ChatHistoryTruncationReducer` if messages exceed context window → resume.

**Docket idempotency**: On review submission, execute `UPDATE Docket SET Status = 'Approved' WHERE EntryId = @id AND Status = 'Pending'` — the `WHERE Status = 'Pending'` clause prevents double-submit races.

**Expiry sweep**: An `IHostedService` running every 30 seconds marks expired Docket entries. Default TTL is 10 minutes, configurable per `IApprovalPolicy` (Standing Order). A SignalR `DocketExpiring` notification is sent 60 seconds before expiry.

---

## 10. Normative Rules Checklist

Use this checklist during code review and plugin development.

- [ ] System prompt is set once at initialization and never modified at runtime (Rule 1)
- [ ] Every tool return is a serialized `ToolEnvelope` — either `ReadResult`, `WriteProposal`, or `ToolError` (Rule 2)
- [ ] Read tools return markdown with `[entity:id](link)` references AND structured `EntityRef[]` (Rule 2)
- [ ] Write tools return `WriteProposal` with an Affidavit and never call `SaveChanges()` or equivalent (Rule 3)
- [ ] Context extraction happens in `ContextExtractor<TTool>` filters, not in prompts (Rule 4)
- [ ] Task inference happens in `TaskInferenceStep` filter, not in prompts (Rule 4)
- [ ] Provider failure is caught and routed to fallback or degraded mode (Rule 5)
- [ ] UI guidance uses `IRouteRegistry` element IDs, not generated CSS selectors (Rule 6)
- [ ] Every `AffidavitField` has a non-null `ProvenanceChain` (Rule 7)
- [ ] Unknown provenance is tagged `ProvenanceSource.Empty`, not omitted (Rule 7)
- [ ] Plugins inject `IServiceScopeFactory`, not scoped services directly (DI lifetime)
- [ ] Plugins catch all exceptions and return `ToolError`, never throw (Error handling)
- [ ] `[Description]` attributes use user-intent language (Tool authoring)
- [ ] `ContextExtractor` is registered for each read tool with entity-rich results (Tool authoring)
- [ ] Docket double-submit is prevented by `WHERE Status = 'Pending'` guard (Persistence)
- [ ] Every `[KernelFunction]` is registered as an `AffiantToolDescriptor` (via attribute or `AddAffiantTool<TStrategy>`) (Tool Descriptor Registry)
- [ ] `Operation.WriteCreate` / `WriteUpdate` descriptors specify both `EntityType` and `InferenceStrategy` (Tool Descriptor Registry)
- [ ] `InferenceStrategy` types are resolvable from `IServiceProvider` (Tool Descriptor Registry)
- [ ] Application startup throws `AffiantStartupException` for any unregistered or unresolvable tool (Tool Descriptor Registry)
- [ ] Every `[AffiantWriteTool]`-decorated `[KernelFunction]` triggers framework-owned pre-tool inference via `InferenceTriggerFilter`; no host-side inference filters (Inference Orchestration)
- [ ] Affidavits are built by `IAffidavitProjection` implementations (default: `SchemaDrivenAffidavitProjection`); never by host-side mapper types reading per-tool form-data structs (Inference Orchestration)
- [ ] The framework's `Affiant.TaskInference` ActivitySource emits one `inference.completed` (or `inference.failed`) event per `(ConversationId, FunctionName, TurnNumber)` tuple (Inference Orchestration)
- [ ] Inference failure never propagates as an exception from the chat loop; `inference.failed` is emitted on the active span and the agent turn completes (Inference Orchestration)

---

## 11. What Is Genuinely Novel vs. Adopted Prior Art

Understanding what to protect and what to adopt is critical for investment decisions.

**Genuinely novel — keep bespoke, invest here**: Field-level provenance tracking with deterministic confidence scoring (no framework offers this). The two-filter context fabric (`ContextExtractor` + `TaskInferenceStep`) that extracts context automatically without developer-defined state schemas. Dual-audience tool returns codified as a framework convention. The Affidavit/Evidence Card/Docket workflow as a first-class architectural pattern.

**Already solved — adopt prior art**: Provider abstraction (SK's `IChatCompletionService`). Review response types (Pydantic AI's `ToolApproved | ToolDenied` pattern). State checkpointing interface (LangGraph's `BaseCheckpointSaver`). Eval testing (promptfoo's YAML + trajectory assertions). Observability (OpenTelemetry GenAI semconv + SK's built-in telemetry). Authorization (ServiceNow's `invoke_from_ai` ACL pattern).

**Partially solved — extend, don't replace**: Human-in-the-loop mechanics (add provenance-aware Evidence Cards and risk-based routing on top). Structured output (add the `ToolEnvelope` discriminated union layer on top of SK's support).
