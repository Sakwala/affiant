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

---

## 11. What Is Genuinely Novel vs. Adopted Prior Art

Understanding what to protect and what to adopt is critical for investment decisions.

**Genuinely novel — keep bespoke, invest here**: Field-level provenance tracking with deterministic confidence scoring (no framework offers this). The two-filter context fabric (`ContextExtractor` + `TaskInferenceStep`) that extracts context automatically without developer-defined state schemas. Dual-audience tool returns codified as a framework convention. The Affidavit/Evidence Card/Docket workflow as a first-class architectural pattern.

**Already solved — adopt prior art**: Provider abstraction (SK's `IChatCompletionService`). Review response types (Pydantic AI's `ToolApproved | ToolDenied` pattern). State checkpointing interface (LangGraph's `BaseCheckpointSaver`). Eval testing (promptfoo's YAML + trajectory assertions). Observability (OpenTelemetry GenAI semconv + SK's built-in telemetry). Authorization (ServiceNow's `invoke_from_ai` ACL pattern).

**Partially solved — extend, don't replace**: Human-in-the-loop mechanics (add provenance-aware Evidence Cards and risk-based routing on top). Structured output (add the `ToolEnvelope` discriminated union layer on top of SK's support).
