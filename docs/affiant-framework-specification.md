# Affiant Framework — Specification & Execution Guide

> **Sworn provenance for every AI write.**  
> **Version**: 1.0.0-spec  
> **Last updated**: 2026-08-03, fix round (§3.12.9 gains the `NextIsToolBody` retry-safety
> mechanism, correcting the disproven "structurally impossible" double-fire claim from the same-day
> P2 ruling-1 landing below; §2.4 records `ManualToolInvoker`'s `FUNCTION_NOT_FOUND` scoping
> correction — see two independent adversarial refuters' findings, `affiant-chancery` review repo)  
> **Previously updated same day**: 2026-08-03, P2 landing (§3.12.4 corrected to the now-enforced
> filter order; new §3.12.9 tool-body/post-processing failure policy; §6 gains the Area 3 gating
> principle; §7.5/tool-authoring-guide.md corrected per affiant#21 — see
> `docs/architecture-review/area-3-tool-calling-reliability.md` (external, in the `affiant-chancery`
> review repo) for the change that drove this update)  
> **Previously updated**: 2026-07-05 (§1 overview, §3.8, §3.12.1, §3.12.3, §3.12.4, §4 Package Mapping, §5 corrected/extended —
> see `docs/proposals/affiant-maf-adapter.md` for the change that drove this update)  
> **Authors**: Software Architect, Technical Product Manager, Principal Engineer, Technical Writer  
> **Status**: Ready for implementation  
> **Repository**: github.com/Sakwala/affiant  
> **Packages**: nuget.org/packages/Affiant.*

---

## 1. What This Document Is

This is the canonical specification for the Affiant framework — a deterministic evidence layer for .NET that provides sworn, field-level provenance tracking between LLMs and databases. It serves as both an architectural reference and the `CLAUDE.md` execution guide for the framework repository.

An affiant is one who swears to truth. This framework swears to the provenance of every field an AI proposes to write.

The framework exists because no agent framework today — open-source or enterprise — offers field-level provenance tracking with deterministic context extraction. Enterprise write operations demand the same evidentiary chain that financial transactions require: the user must know *why* the AI suggested each value before approving it. Affiant intercepts every AI-proposed database mutation that flows through a *locally-invoked* tool call, tags each field with its deterministic origin, and holds the proposal in a durable review queue — the **Docket** — for human review. Nothing commits without evidence. Nothing writes without approval. "Locally-invoked" excludes a backend's own hosted/server-side tools (code interpreter, web/file search, and similar) that the backend executes without surfacing a client-side call Affiant can intercept: under MAF this boundary is explicit and audited (`docs/adapters/microsoft-agent-framework.md` §6, "The hosted-tool boundary"); SK's interception seam has the analogous scope, since it likewise only sees client-visible function calls.

Affiant's interception logic — provenance tagging, task inference, and review gating — is defined once, backend-neutrally, and runs behind either of two interception backends: Semantic Kernel's `IFunctionInvocationFilter`/`IAutoFunctionInvocationFilter` pipeline (the original, richest-available interception surface at the time this framework was built) or the Microsoft Agent Framework's function-calling middleware (added 2026-07-05; see §3.12.3 and `docs/adapters/microsoft-agent-framework.md`). It cleanly separates into six architectural layers, with the natural boundary between framework and host application falling at four seams: domain plugins, domain models, transport configuration, and the interception backend itself (§5).

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

**`ToolError.Code` registry (added 2026-08-03, area-3 P2 ruling 4).** `Code` is a bare `string` —
until this addition it had no enum, no shared constants class, and no contract test, so a host
code could silently collide with a framework code
(`docs/architecture-review/area-3-tool-calling-reliability.md` V6, `affiant-chancery` review repo).
`Affiant.Abstractions.Models.ToolErrorCodes` now declares every code the framework itself emits
(`DB_TIMEOUT`, `UPSTREAM_UNAVAILABLE`, `VALIDATION_FAILED`, `UNKNOWN` — from
`ToolErrorFilter.MapExceptionToToolError` — plus `REVIEW_FILING_FAILED` and `FUNCTION_NOT_FOUND`;
see that type's own remarks for scope). Hosts declare their own `ToolErrorCodes`-style class for
their domain codes and opt into
`Affiant.Testing.ComplianceHarness.ComplianceHarness.AssertToolErrorCodeRegistryParity` — the same
additive, opt-in pattern as `AssertToolNameRegistryParity`/`AssertFabricKeyParity` — to get
drift-failure the same way. Declaring this registry does not require any host to adopt it and does
not change any code a host already has; host-side adoption of a host's OWN domain codes is deferred
to the Area 3 closing wave.

**Framework-side adoption completed, fix round (2026-08-03).** `FUNCTION_NOT_FOUND` is a framework
code — the P2 wave above wrongly grouped `ManualToolInvoker`'s hand-written JSON literal for it with
host-side adoption and deferred it; that scoping error is corrected here.
`ManualToolInvoker.CaptureAndInvokeAsync` now builds its not-found payload through the real
`ToolError` type consuming this constant, not a hand-written JSON string. Because
`AssertToolErrorCodeRegistryParity`'s `emittedCodes` parameter is caller-supplied by design, a
self-check built from the same constants it verifies against can only ever catch an orphaned
constant, never a new bare-literal emission site (proven by mutation: an adversarial refuter added a
rogue `"RATE_LIMITED"` classification arm to `MapExceptionToToolError` and no existing test failed).
`Affiant.Testing.ComplianceHarness.Tests.AssertToolErrorCodeSourceScanTests` closes that gap: it
reads the framework's own `src/` tree from disk and fails on any bare string literal in the three
shapes a `ToolError` code emission site can take in this codebase (a named `Code: "LITERAL"`
argument, a `(code, retryable)` classification-tuple arm, or a hand-rolled JSON `"code":"LITERAL"`
field) — proven to catch both the rogue-arm mutation and a reverted `ManualToolInvoker` literal, and
restored byte-identical after each proof.

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
public enum ReviewStatus { Pending, Approved, Rejected, Expired, Deferred }

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
    IReadOnlyDictionary<string, object?>? Amendments,  // Fields the reviewer changed; null value = explicitly cleared
    Guid? ResubmittedTo = null                 // Set once, by ConsumeForResubmitAsync, when this
                                                // (Expired) entry is resubmitted — see "Resubmission
                                                // and lineage" below
);
```

**Amendment round-trip (issue #6, GA exit criterion).** The reviewer's actual edits arrive on
`EvidenceCardResponse.Amendments` (transported via `TransportEvent.EvidenceCardResponse`) rather
than at filing time — `ReviewContext.Amendments` on `DocketEntry` creation is a distinct,
earlier-stage input (e.g. pre-filled defaults). `ReviewGate` persists `EvidenceCardResponse.Amendments`
onto the `DocketEntry` via `IDocketStore.UpdateAmendmentsAsync(entryId, amendments, ct)` once the
approval transition has won the double-submit race (§ below, "Docket idempotency"). `Status` stays
`Approved` throughout this round-trip — `ReviewStatus` has no distinct value for "approved with
amendments" (nor for withdrawal); an amended approval is fully described by `Status == Approved`
plus a non-null `Amendments`, and no code path ever transitions `Status` on account of an amendment.
Framework responsibility ends there: appending a UserStated `ProvenanceTag` (`ProvenanceTag.FromUser`,
Rule 7) to each amended field's `ProvenanceChain` before the write reaches the domain store is the
host's `IWriteExecutor` overlay's job — `IWriteExecutor.ExecuteAsync(affidavit, amendments, ct)`
already accepts the amendments dictionary for exactly that purpose. A test asserting the persisted
chain ends in `UserStated` therefore belongs in the host's test suite, once the overlay exists,
not in `Affiant.Testing.ComplianceHarness` (which asserts task-inference extraction substance, an
unrelated pipeline stage).

**Resubmission and lineage (Area-5 Decision 2, affiant#31).** `ReviewGate.ResubmitAsync(expiredEntryId, ct)`
lets a caller retry a review whose `DocketEntry` has already gone `Expired`: it mints a brand-new
`EntryId`, clones the expired entry's `Envelope` and (as `EvidenceCardRequest.PriorAmendments`) its
`Amendments` into a fresh `Pending` entry, and broadcasts a new Evidence Card. There is no
`ReviewStatus.Resubmitted` — a resubmitted entry's `Status` stays `Expired` forever, matching the
reference client's own product decision to never visually distinguish a resubmitted card from a
plain expired one (the two facts — "why did this end" and "was it superseded" — are kept
independent, matching how Temporal's Continue-As-New and Stripe's `parent` field each model
supersession as a separate reference rather than an overloaded status).

`ResubmittedTo` is a nullable `Guid` on the *source* (expired) entry, set exactly once via
`IDocketStore.ConsumeForResubmitAsync(entryId, newEntryId, ct)` — a guarded
`WHERE Status = 'Expired' AND ResubmittedTo IS NULL` conditional update returning the same 0/1
rows-affected idiom every other status transition uses. `ResubmitAsync` calls this guard *before*
filing the new entry, so it — not the filing — is what two concurrent resubmit attempts on the same
expired entry actually race on: exactly one call wins the claim; the other sees 0 rows affected and
throws `InvalidOperationException`, the same shape as an unknown or non-Expired `expiredEntryId`.
`ResubmittedTo` doubles as queryable lineage: `IDocketStore.GetResubmissionParentAsync(entryId, ct)`
is the reverse lookup (find the entry whose `ResubmittedTo` equals a given entry's id), used to
re-derive `PriorAmendments` for a freshly-resubmitted `Pending` entry on reconnect — closing a
silent-loss window, since `EvidenceCardRequest.PriorAmendments` only ever travels on the original,
transient resubmission broadcast and is never itself persisted onto the new entry's `Amendments`.

Filing the new entry after a successful claim is not itself guarded by the same transaction — if it
fails (store outage, cancellation), the source entry's `ResubmittedTo` is left pointing at an
`EntryId` no `DocketEntry` row was ever created for. This is accepted as a documented, logged,
operator-visible failure mode rather than compensated with an automatic rollback, since a rollback
would itself need to race safely against a subsequent resubmit attempt — reopening the exact problem
the guard exists to close. See `ReviewGate.ResubmitAsync`'s remarks for the full contract.

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

> Corrected 2026-07-31 alongside the Rule 6 clarification above: this record previously
> documented a `Selector`/`Route`/`Description`/`Tags` shape that never matched the shipped type
> and — worse — modeled a CSS selector as a first-class field, directly contradicting Rule 6. The
> text below reflects the interface as it actually ships in
> `src/Affiant.Abstractions/Models/GuidableElement.cs`. A host that wants route-scoping or a
> human-readable description stores them as `Attributes` entries (e.g. `"route"`, `"displayName"`)
> — see `MeridianRouteRegistry` in the private `affiant-host-apps` repo for the live end-to-end
> reference implementation of that convention (registry, `UiGuidancePlugin`, and frontend renderer
> all wired together). `HRPortalRouteRegistry` implements the same `IRouteRegistry` convention but
> is registry-only as of 2026-07-31 — no HRPortal plugin emits `GuideUI` yet, so it has no bridge
> or emitting plugin exercising it end-to-end.

```csharp
public sealed record GuidableElement(
    string ElementId,                            // Stable semantic identifier (e.g., "save-button")
    string ElementType,                          // Element kind for the consumer (e.g., "button", "form", "widget")
    Dictionary<string, object>? Attributes = null // Host-defined metadata: displayName, description,
                                                   // route, rendering hints (side, highlightPadding), etc.
                                                   // Never a CSS/DOM selector — see Rule 6.
);
```

### 2.10 Event Vocabulary (Enum)

> **Rewritten 2026-08-04 (area-4 architecture review, proposal P1g) to match the shipped enum.**
> The previous revision of this section was written 2026-04-11 — 19 days *before* the real
> `TransportEvent` enum was first implemented (2026-04-30) — and was never reconciled with the
> code afterward: it listed six members that were never built (`AgentTyping`, `AgentChunk`,
> `ToolCallStarted`, `ToolCallCompleted`, `SessionRehydrated`, `Error`) and omitted two that were
> (`AgentMessage`, `ContextUpdate`). Anyone reading the prior revision to understand the transport
> contract was reading a document that predated the artifacts it claimed to describe.

**`TransportEvent`** — the framework's explicit enum for all server→client wire events; a plain
integer internally, never serialized as an integer over the wire. Each member is translated to a
SignalR client method name (the string the browser's `connection.on(methodName, ...)` handler is
registered under) by `TransportEventExtensions.ToClientEventName()` (package
`Affiant.Transport.SignalR`) — a *total* mapping: every member has an explicit case, adding a new
member without a matching case is a compiler error, and the method is `public`, so a host's own
contract tests can call it directly instead of only through reflection.

```csharp
public enum TransportEvent
{
    EvidenceCardRequest,   // -> "ConfirmAction" — framework broadcasts a write proposal awaiting
                           // human review to the UI. Payload: the EvidenceCardRequest record
                           // (Affiant.Abstractions.Transport), carrying the Affidavit under review.
    EvidenceCardResponse,  // -> "EvidenceCardResponse" — reserved for the document-reserved
                           // blocking review path (§3.1); production traffic delivers the
                           // reviewer's decision through a host hub RPC method instead, never
                           // this broadcast direction.
    AgentMessage,          // -> "ReceiveToken" — one streamed text chunk from the agent.
    ContextUpdate,         // -> "ContextUpdated" — framework notifies the UI that conversation
                           // context changed.
    SystemNotification,    // -> "SystemNotification" — transient notification (error, warning,
                           // info). Payload: Affiant.Abstractions.Transport.SystemNotificationPayload
                           // (Level, Message) — Level is a plain string, not a C# enum; its
                           // allowed values are pinned by the host contract net, not this type.
    DocketExpiring,        // -> "DocketExpiring" — a Pending DocketEntry (§2.7) is approaching
                           // its review TTL. Payload: DocketExpiringNotification.
    DocketExpired,         // -> "DocketExpired" — a Pending DocketEntry transitioned to Expired
                           // without a reviewer decision. Payload: DocketExpiredNotification.
    UiGuidance             // -> "GuideUI" — starts a UI guidance walkthrough (Rule 6, §6).
                           // Payload: Affiant.Abstractions.Transport.UiGuidancePayload. The wire
                           // name "GuideUI" is pinned to match a reference host's existing client
                           // listener — see §6's Rule 6 note for why.
}
```

**Historical note.** The founding commit that first implemented this enum (2026-04-30) also
defined a `UserMessage` member (the fourth of its original eight), added in the same commit and same line range as
`AgentMessage` with no independent design note. It was deleted 2026-08-04 (proposal P1a):
inbound chat text has always entered the framework as a host-defined SignalR hub RPC method (for
example `SendMessage(message, conversationId)` — SignalR's own client→server invoke pattern),
never as a broadcast `TransportEvent`; the member had no production emitter in the framework or
either reference host it was checked against.

---

## 3. Interface Contracts

These are the extension points that host applications and optional adapters implement. The framework depends on these interfaces — never on concrete implementations.

### 3.1 Transport

> **Rewritten 2026-08-04 (area-4 architecture review, proposal P1g) to match the shipped interface.**
> The previous revision, written 2026-04-11, documented only `IStreamingTransport`'s first-cut three
> methods and was never updated for the same-day (2026-04-30) additions of a blocking-await method
> and its decision-delivery counterpart.

```csharp
public interface IStreamingTransport
{
    // Sends eventType to the single client identified by connectionId.
    Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct);

    // Sends eventType to every client in the named SignalR group (a session group or a reviewer
    // group — the naming conventions and typed broadcast helpers live on the AffiantHub base class
    // in Affiant.Transport.SignalR; see that class's XML docs). Every framework service that pushes to a client (ReviewGate,
    // ReviewGateFilter, DocketExpiryService, UiGuidanceBridge) uses this method, never a raw
    // SignalR Clients.Group(...).SendAsync(...) call.
    Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct);

    // Document-reserved (proposal P1a) — see the remarks below. Not the production default.
    Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(
        string sessionGroupId, Guid docketId, CancellationToken ct = default);

    // Routes a reviewer's decision to a live AwaitEvidenceCardResponseAsync waiter for docketId.
    // Returns false (the default) if no waiter exists — the caller should use the docket-replay
    // path (ReviewGate.HandleDecisionAsync) instead.
    bool TryDeliverResponse(Guid docketId, EvidenceCardResponse response) => false;
}
```

**`AwaitEvidenceCardResponseAsync` — document-reserved, not the production path.** This method
blocks the calling task until the reviewer's `EvidenceCardResponse` for `docketId` arrives. It
backs `ReviewGate.FileReviewAsync` — the blocking half of the review state machine, and the
"intentional async cycle" named in §4 Layer 4 below. It is proven, in a live production session
(2026-07-31, incident `affiant-host-apps#25`), to deadlock over the framework's only shipped
transport: SignalR's `HubOptions.MaximumParallelInvocationsPerClient` defaults to `1` and is never
overridden by either reference host, so the one hub invocation blocked here awaiting a decision
holds the connection's only invocation slot — the very slot the decision's own delivery (a
separate hub RPC invocation, e.g. `ApproveAction`) needs in order to arrive. The production default
is `ReviewGateFilter` (§6 Rule 3) calling the non-blocking `ReviewGate.FileForReviewAsync` instead
and ending the model's turn when a decision requires human review; the eventual decision then
reaches `ReviewGate.HandleDecisionAsync` through a separate hub RPC, never through this method's
await. A sound redesign of the blocking path — the decision traveling on a channel other than the
blocked connection — is tracked as a design ticket, `affiant#29` (filed 2026-08-04, no
implementation planned as of that date); do not reach for `AwaitEvidenceCardResponseAsync` in new
code until it lands.

**What this interface no longer includes.** A prior revision of this section also declared
`IAsyncEnumerable<TransportMessage> ReceiveAsync(string connectionId, CancellationToken ct)`, and
the framework once shipped a matching `TransportMessage` double-JSON envelope type. Both were
deleted 2026-08-04 (proposal P1a): they were scaffolding for a second, pull-based transport (the
`TransportMessage` type's own doc comment named "SignalR, WebSocket, etc." as the intended targets)
that was never built in the three months since the framework's first commit; the framework's one
shipped transport implementation had thrown `NotSupportedException` unconditionally from
`ReceiveAsync` since the method was first written, and no call site anywhere ever reached it.

### 3.2 Persistence

```csharp
// Modeled after LangGraph's BaseCheckpointSaver pattern
public interface IChatSessionStore
{
    Task<ChatSession> CreateAsync(string tenantId, string userId, CancellationToken ct);
    Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct);
    Task SaveMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct);
    Task AppendMessagesAsync(string sessionId, IReadOnlyList<AffiantChatMessage> messages, CancellationToken ct);
    Task<IReadOnlyList<AffiantChatMessage>> LoadMessagesAsync(string sessionId, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);
}
```

**Two write classes (Area-5 Decision, proposal P2a, affiant#27).** `SaveMessagesAsync` is the
rehydration-class write: it replaces every message stored for a session in full, which the
SQLite/Postgres implementations realize as delete-and-reinsert — a second concurrent
`SaveMessagesAsync` call working from a stale snapshot can silently drop the first caller's
messages. `AppendMessagesAsync` is the turn-save-class write added to close that window for the
common case: it adds messages after whatever is already durable, continuing the session's
`Ordinal` at `MAX + 1`, as one transaction, and never reuses `SaveMessagesAsync`'s delete-and-
reinsert path. Turn-by-turn persistence must use `AppendMessagesAsync`; only rehydration-style
callers that already hold the complete, authoritative message list (e.g. after a truncation pass on
reconnect) use `SaveMessagesAsync`. `Affiant.EntityFramework` ships three implementations —
`SqliteChatSessionStore`, `PostgresChatSessionStore`, and `InMemoryChatSessionStore` — bringing the
chat-session side to the same three-store shape `IDocketStore` already has (`AddAffiantEntityFramework`
selects one via `UsePostgres`/`UseSqlite`/`UseInMemory`).

// The Docket — the durable review queue
public interface IDocketStore
{
    Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct);
    Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct);
    Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct);
    Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct);
    Task UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct);
    Task UpdateAmendmentsAsync(Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct);
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

> Corrected 2026-07-31 alongside the Rule 6 clarification in §6: `GetAll()` never matched the
> shipped method name, and `GetElementById` was missing entirely. The text below reflects
> `src/Affiant.Abstractions/Interfaces/IRouteRegistry.cs` as it actually ships. This is the single
> supported UI guidance model — see the Rule 6 note in §6 for what "single supported" rules out.

```csharp
public interface IRouteRegistry
{
    void Register(GuidableElement element);
    IReadOnlyList<GuidableElement> GetElementsForRoute(string route);
    IReadOnlyList<GuidableElement> GetAllElements();
    GuidableElement? GetElementById(string elementId);
}
```

### 3.8 Intent Interception

> Corrected 2026-07-05: this section previously documented a `Priority`/`CanHandle(string)` shape
> that never matched the shipped interface. The text below now reflects the interface as it
> actually ships in `src/Affiant.Abstractions/Interfaces/IIntentInterceptor.cs` — a drift
> discovered during the `Affiant.AgentFramework` adapter recon (`docs/proposals/affiant-maf-adapter.md`
> §4.3) and fixed alongside that work. The shipped interface takes an arguments dictionary and
> has no priority-ordering concept: `DeterministicShortCircuit` (§4, Layer 2) queries every
> registered `IIntentInterceptor` and uses the first one whose `MatchesAsync` returns `true`, in
> DI registration order.

```csharp
// For DeterministicShortCircuit — bypasses the LLM entirely for high-failure-cost intents
public interface IIntentInterceptor
{
    Task<bool> MatchesAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    Task<object?> HandleAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);
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

> Added 2026-05-14 as part of Phase 3 Track A Epic 15 (stories 15.1–15.7). Closes the empty-Affidavit regression identified at commit `b72c1fa` (2026-04-30) and recorded in `docs/proposals/affiant-validator-handoff.md` (in the private Sakwala/affiant-host-apps repo).

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

**Why open record, not enum?** A closed enum forces every host to wait for a framework release cadence to introduce a new operation kind. The open-record contract decouples host extensibility from framework release tempo. (Decision D27, documented in `docs/proposals/affiant-validator-handoff.md` (in the private Sakwala/affiant-host-apps repo) §10 — D27 reads: "Operation as open record, not enum, to permit host-defined operation kinds without a framework version bump.")

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

`AllowMultiple = false` is enforced — applying the attribute twice to the same method is a compile-time error. The attribute name, namespace, constructor parameter order, and `AllowMultiple` value are part of the public API contract ratified at HIL gate G0 (2026-05-14, `docs/implementation-artifacts/track-a/g0-descriptor-contract-approval.md` (in the private Sakwala/affiant-host-apps repo) Item 4).

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

The structural reason: before the validator existed, the framework silently produced empty Affidavits when a write tool was misclassified. An Affidavit with all fields at `ProvenanceSource.Empty` is indistinguishable from a read tool's correct provenance — the error was invisible at runtime and surfaced only in audit reviews. The 2026-04-30 regression at commit `b72c1fa` demonstrated that a warning-and-continue approach does not protect against this class of misconfiguration. The validator is the load-bearing fix. See also: PRD Task 6 preamble in `docs/architecture/phase-3-prd-a0-tool-descriptor-registry.md` (in the private Sakwala/affiant-host-apps repo).

Both error-message shapes were ratified as part of the public API contract at HIL gate G0 (2026-05-14, `docs/implementation-artifacts/track-a/g0-descriptor-contract-approval.md` (in the private Sakwala/affiant-host-apps repo) Item 5).

#### 3.11.6 Adopter Integration Paths

A host has exactly two supported paths to register a tool. Both are equivalent and may be mixed within a single host.

**(a) Attribute-driven.** Decorate the `[KernelFunction]` method with `[AffiantWriteTool(operation, entityType, typeof(TStrategy))]`, then call `kernelBuilder.AddAffiantPluginsFromAssembly(typeof(SomeHostType).Assembly, pluginName: "…")`. The walker registers descriptors for every `[KernelFunction]` in the assembly: writes by attribute presence, reads by attribute absence. The strategy type must still be registered separately in DI (e.g., `services.AddSingleton<TStrategy>()`) so Check B passes.

**(b) Explicit DI.** Call `services.AddAffiantTool<TStrategy>("FunctionName", Operation.WriteCreate, "EntityType")` for each write tool, or `services.AddAffiantReadTool("FunctionName")` for each read tool. This path registers both the strategy and the descriptor atomically — no separate `AddSingleton` call required.

The registry's idempotency contract (double-registration throws `InvalidOperationException`) catches accidental overlap when both paths are used in the same host for the same tool.

---

### 3.12 Inference Orchestration & Affidavit Projection

> Added 2026-06-12 as part of Phase 3 Track A Epic 16 (stories 16.1–16.6), ratified 2026-05-05. Addresses the empty-Affidavit regression identified at commit `b72c1fa` (2026-04-30) and recorded in `docs/proposals/affiant-validator-handoff.md` (in the private Sakwala/affiant-host-apps repo).

The L2 inference orchestration layer centralizes two responsibilities that were previously scattered across host implementations: (1) running structured-output inference *before* a write tool executes (pre-tool), so the LLM's intent is captured while the conversation history still reflects the user's unmodified request; and (2) building the resulting `Affidavit` directly from the `ContextFabric` — rather than from per-tool form-data structs that hosts previously had to maintain. The 2026-04-30 regression at commit `b72c1fa` demonstrated why both matters: when inference was decomposed into a post-tool filter, structured-output JSON was parsed from the tool's *return value* where it never existed, and every Affidavit produced was silently fully `ProvenanceSource.Empty`. L2 restores pre-tool inference as a framework concern, preventing the regression class entirely. The architecture was ratified 2026-05-05 (decision D21 — L2 over L1/L3 alternatives; see `docs/proposals/affiant-validator-handoff.md` §10 for the decision rationale).

**Glossary for this section:**

- *Affidavit* — the framework's field-level provenance record attached to every write operation. Each field carries a `ProvenanceChain`. `ProvenanceSource.Empty` marks fields whose origin could not be determined (Rule 7 — see §6).
- *ContextFabric* — the framework's conversation-scoped in-memory entity accumulation store (registered `Scoped` by `AddAffiantCore()`, one instance per turn; §3.12.3). Read tools extract entities into it via `ContextExtractor<TTool>` filters; L2 inference reads from it to build Affidavit fields.
- *`[AffiantWriteTool]`* — the attribute that marks a `[KernelFunction]` as a write-intent tool and associates it with an `InferenceStrategy` type and an `EntityType` string (§3.11.4 above).
- *`ITaskInferenceStrategy`* — the host-supplied strategy interface that declares which fields the framework should infer for a given entity type (§3.10 Task Inference Strategy).
- *`IAffiantToolRegistry`* — the registry that maps `(FunctionName, PluginName)` pairs to `AffiantToolDescriptor` records, including the associated `InferenceStrategy` type (§3.11.3 above).

**External dependencies this section presumes:**

- `Microsoft.SemanticKernel` — the SK kernel's `IFunctionInvocationFilter` and `IAutoFunctionInvocationFilter` pipeline that hosts the L2 filters.
- `System.Diagnostics.ActivitySource` — the .NET OTel instrumentation primitive used by the `Affiant.TaskInference` ActivitySource.
- `Microsoft.Extensions.DependencyInjection` — used to resolve `ITaskInferenceStrategy` implementations from `IServiceProvider` at orchestration time.

#### 3.12.1 The Three New Contracts

L2 introduces three new abstractions in `Affiant.Abstractions.Interfaces`, each with a default implementation in `Affiant.Core` or `Affiant.SemanticKernel`.

**`IInferenceCompletionPort`** is the port through which the framework sends a structured-output inference request to an LLM. Its single method, `CompleteStructuredAsync(InferenceCompletionRequest) → JsonElement`, accepts a request bundle (conversation history, the active `ITaskInferenceStrategy`, the function name, and the current tool arguments) and returns a `JsonElement` whose schema matches the strategy's declared fields. The framework ships two implementations, one per interception backend: `SemanticKernelInferenceCompletionPort` in `Affiant.SemanticKernel` and `AgentFrameworkInferenceCompletionPort` in `Affiant.AgentFramework` (added 2026-07-05; see §3.12.3). Hosts that want to route inference through a different LLM provider — or stub it in tests — replace the port via DI without touching any other L2 component.

**`IInferenceTrigger`** decides, per tool invocation, whether inference should run. Its single method, `ShouldRun(InferenceTriggerContext) → bool`, receives the function name, plugin name, current tool arguments, the active `ContextFabric`, and the invocation phase (`PreTool`). The framework registers one default trigger: `WriteIntentInferenceTrigger`, which returns `true` for any tool whose `AffiantToolDescriptor` has `Operation.Kind` equal to `"WriteCreate"` or `"WriteUpdate"`. Hosts may register additional triggers via DI; `InferenceTriggerFilter` short-circuits on the first trigger that returns `true`.

**`IAffidavitProjection`** constructs the Affidavit for a given entity type after inference results are merged into the `ContextFabric`. Its `Project(IContextFabric, operationType, warnings) → Affidavit` method reads fields from the fabric, applies `IDeterministicFieldSource` overrides (see below), and falls back to `ProvenanceTag.Empty` for any field the fabric cannot satisfy (Rule 7). The default implementation is `SchemaDrivenAffidavitProjection` in `Affiant.Core`.

**`IDeterministicFieldSource`** is an augmentation surface for fields that should always come from a deterministic source (e.g., a system clock, a session-authenticated user ID) rather than from LLM inference. `SchemaDrivenAffidavitProjection` checks registered `IDeterministicFieldSource` implementations per field before consulting the fabric; the first non-null resolution wins.

*Source files:* `src/Affiant.Abstractions/Interfaces/IInferenceCompletionPort.cs`, `src/Affiant.Abstractions/Interfaces/IInferenceTrigger.cs`, `src/Affiant.Abstractions/Interfaces/IAffidavitProjection.cs`, `src/Affiant.Abstractions/Interfaces/IDeterministicFieldSource.cs`

#### 3.12.2 Default Services

Three default service implementations ship with the framework. Hosts that accept the defaults need only call `AddAffiantInferenceOrchestration()` (§3.12.3) during DI setup.

**`TaskInferenceRunner`** (in `Affiant.Core.Services`) is the stateless orchestrator that bridges `IInferenceCompletionPort` and the merge step. It builds an `InferenceCompletionRequest`, calls the port, forwards the resulting `JsonElement` to `TaskInferenceStep` for confidence-based merge into the `ContextFabric`, and emits the `inference.completed` span event. On any non-cancellation exception it emits `inference.failed`, logs a warning at `LogWarning` level, and returns an empty `TaskInferenceResult` — the fail-safe contract (§3.12.7).

**`WriteIntentInferenceTrigger`** (in `Affiant.Core.Triggers`) is the default `IInferenceTrigger` registered by `AddAffiantInferenceOrchestration()`. It fires inference for any tool whose registered `AffiantToolDescriptor` has `Operation.Kind` of `"WriteCreate"` or `"WriteUpdate"`.

**`SchemaDrivenAffidavitProjection`** (in `Affiant.Core.Services`) is the default `IAffidavitProjection`. It iterates the fields declared by the active `ITaskInferenceStrategy`, applies `IDeterministicFieldSource` overrides first, then reads from the `ContextFabric`, and falls back to `ProvenanceTag.Empty` for any unresolved field (Rule 7). After projection it emits the `affidavit.projected` span event and publishes a typed `AffidavitEmittedEvent` through `IObservabilityEventStream<AffidavitEmittedEvent>` for downstream subscribers.

**`FunctionNameInferenceTrigger`** (in `Affiant.Core.Triggers`) is a soft-deprecated `IInferenceTrigger` that fires by explicit function-name allowlist rather than registry classification. It exists to support hosts adopted before the Tool Descriptor Registry (§3.11) was available, carries an `[Obsolete]` attribute, and will be removed before v1.0.0. Hosts should migrate to `WriteIntentInferenceTrigger` with `[AffiantWriteTool]` decoration.

*Source files:* `src/Affiant.Core/Services/TaskInferenceRunner.cs`, `src/Affiant.Core/Triggers/WriteIntentInferenceTrigger.cs`, `src/Affiant.Core/Services/SchemaDrivenAffidavitProjection.cs`, `src/Affiant.Core/Triggers/FunctionNameInferenceTrigger.cs`

#### 3.12.3 Neutral Pipeline + Backend Bridges

> Rewritten 2026-07-05 (`docs/proposals/affiant-maf-adapter.md`, implemented on branch
> `feat/agent-framework-adapter`, commits `50e6cf2`/`649dd32`). This section previously claimed
> the L2 SK-specific components were isolated to `Affiant.SemanticKernel` and that L2 AC #4
> ("`Affiant.Core` must not take a direct SK dependency," ratified 2026-05-05) already held. As
> written, that claim was **false**: `Affiant.Core` carried a direct `Microsoft.SemanticKernel`
> PackageReference and several filters implemented SK's own filter interfaces directly, and
> `Affiant.Abstractions` was typed against SK's `ChatMessageContent`/`ChatHistory`. The 2026-07-05
> refactor made the claim true and test-enforced. What follows describes the **current, shipped**
> architecture, not the earlier aspiration.

L2's filter logic is defined **once**, backend-neutrally, in `Affiant.Core`, against a neutral
invocation contract in `Affiant.Abstractions` (`ToolInvocationContext`, `IToolInvocationFilter` —
see `src/Affiant.Abstractions/Models/ToolInvocationContext.cs` and
`src/Affiant.Abstractions/Interfaces/IToolInvocationFilter.cs`). Each interception backend package
(`Affiant.SemanticKernel`, `Affiant.AgentFramework`) is a **thin bridge**: it translates its
framework's native tool-invocation seam into a `ToolInvocationContext`, hands it to a shared
`ToolInvocationPipeline` (`Affiant.Core.Services`) which runs the canonical filter order
(§3.12.4), and translates the outcome back. Bridges contain no provenance, inference, or
review-gate logic — that logic exists in exactly one place, so the two backends cannot drift
apart in what they tag or when they gate. `Affiant.Abstractions` and `Affiant.Core` now carry no
`Microsoft.SemanticKernel` or `Microsoft.Agents.AI` PackageReference at all — **L2 AC #4 holds
and is enforced by the domain-agnostic/dependency static-analysis test suite**, not merely
asserted in prose.

`ToolInvocationContext` (neutral, in `Affiant.Abstractions.Models`) carries `FunctionName`,
`PluginName`, a mutable `Arguments` dictionary, a settable `Result`, a `Terminate` flag, the
`Services` provider the pipeline resolved filters from, and ambient turn context (`ConversationId`,
`TurnNumber`, `History` as `IReadOnlyList<AffiantChatMessage>`) that each bridge populates from
whatever its framework exposes.

**Scope flow.** `ToolInvocationPipeline` resolves its filters — and, transitively, the
conversation-scoped `ContextFabric` — from the **caller's ambient service scope** when the bridge
supplies one, owning a fresh scope per invocation only as a fallback. This is deliberate: `ContextFabric`
/ `IContextFabric` is registered `Scoped` by `AddAffiantCore()` (one instance per conversation turn),
so every filter in a tool call — and, on SK, both the invocation-stage and completion-stage bridges of
that call — must resolve from one scope to share one fabric, while concurrent turns (distinct scopes)
stay isolated. The SK bridges pass `context.Kernel.Services`; the MAF middleware passes
`AIFunctionArguments.Services` when the host wired one, else lets the pipeline own a per-invocation
scope. A singleton fabric would share one un-namespaced store across all concurrent conversations
(value bleed) and let `Clear()` race a live projection to `ProvenanceTag.Empty`; hosts MUST NOT
re-register it as a singleton (tool-authoring guide §4.1). `AffiantChatMessage` (also neutral) replaced SK's `ChatMessageContent` in
`IChatSessionStore`, `InferenceCompletionRequest`, and `InferenceFixtureCase`; each bridge
converts to/from its native message type at its own edge (`SkMessageConversions` for SK,
`MafMessageConversions` for MAF).

**`TaskInferenceRunner`, `ToolArgumentCaptureFilter`, `InferenceTriggerFilter`,
`TaskInferenceMergeFilter`, `ReviewGateFilter`, `ToolErrorFilter`, `ToolTracingFilter`,
`DeterministicShortCircuit`, and `ContextExtractor<T>`** all live in `Affiant.Core` today (moved
from `Affiant.SemanticKernel`, or re-expressed off SK types, by the 2026-07-05 refactor) and
implement the neutral `IToolInvocationFilter` contract rather than any SK-specific interface.

**The SK bridge** (`Affiant.SemanticKernel`) keeps the exact two-firing-position split SK's own
filter pipeline requires — pre-tool argument capture and inference triggering at SK's
`IFunctionInvocationFilter` position, post-tool merge and review gating at SK's
`IAutoFunctionInvocationFilter` position (where SK exposes auto-invocation-loop termination) —
via two concrete bridge classes: `AffiantFunctionInvocationBridge`
(`src/Affiant.SemanticKernel/Filters/AffiantFunctionInvocationBridge.cs`, implements SK's
`IFunctionInvocationFilter`) and `AffiantAutoFunctionInvocationBridge`
(`src/Affiant.SemanticKernel/Filters/AffiantAutoFunctionInvocationBridge.cs`, implements SK's
`IAutoFunctionInvocationFilter`). Each constructs a `ToolInvocationContext`, runs the neutral
`ToolInvocationPipeline` with the subset of neutral filters appropriate to its firing position,
and writes the result back — for the invocation bridge, into the SK context's mutable `Result`;
for the auto-invocation bridge, likewise, plus mapping `Terminate` onto
`AutoFunctionInvocationContext.Terminate`.

**`SemanticKernelInferenceCompletionPort`** (in `Affiant.SemanticKernel.Adapters`) implements
`IInferenceCompletionPort` using SK's `IChatCompletionService` with structured-output mode,
unchanged in role by this refactor beyond converting through `AffiantChatMessage` at its edge.

**`AddAffiantInferenceOrchestration()`** (in `Affiant.SemanticKernel.Extensions.ServiceCollectionExtensions`)
registers the SK-side L2 components: `SemanticKernelInferenceCompletionPort`, `TaskInferenceRunner`,
`WriteIntentInferenceTrigger`, the neutral `ToolArgumentCaptureFilter`/`InferenceTriggerFilter`
(now registered against `IToolInvocationFilter`, not SK's `IFunctionInvocationFilter`),
`SchemaDrivenAffidavitProjection`, and the SK-adapter startup validator extension.
`AddAffiantSkFilters()` registers the two bridge classes themselves against SK's own filter
interfaces — this is the one place SK's interfaces are referenced at all.

**The MAF bridge** (`Affiant.AgentFramework`, added 2026-07-05) is the second, symmetric proof
that the neutral pipeline is genuinely backend-neutral: MAF exposes exactly one function-calling
seam (no invocation/auto-invocation split), so `AffiantFunctionInvocationMiddleware`
(`src/Affiant.AgentFramework/Filters/AffiantFunctionInvocationMiddleware.cs`) runs the *entire*
canonical filter order at that one seam and seals evidence by **returning** the neutral context's
`Result` from the middleware delegate (MAF's `FunctionInvocationContext` has no settable
`.Result`, unlike SK's context). See `docs/adapters/microsoft-agent-framework.md` for the full
host-facing surface.

*Source files:* `src/Affiant.Abstractions/Models/ToolInvocationContext.cs`,
`src/Affiant.Abstractions/Interfaces/IToolInvocationFilter.cs`,
`src/Affiant.Core/Services/ToolInvocationPipeline.cs`,
`src/Affiant.SemanticKernel/Filters/AffiantFunctionInvocationBridge.cs`,
`src/Affiant.SemanticKernel/Filters/AffiantAutoFunctionInvocationBridge.cs`,
`src/Affiant.SemanticKernel/Adapters/SemanticKernelInferenceCompletionPort.cs`,
`src/Affiant.SemanticKernel/Extensions/ServiceCollectionExtensions.cs`,
`src/Affiant.AgentFramework/Filters/AffiantFunctionInvocationMiddleware.cs`

#### 3.12.4 Pipeline Order

> Restated 2026-07-05 as backend-neutral (`docs/proposals/affiant-maf-adapter.md` §3). The order
> itself is unchanged from when it was first locked (Story 16.4); what changed is that it is now
> a property of the one neutral `ToolInvocationPipeline` (§3.12.3) that both backends run,
> rather than of an SK-specific filter chain. The onion-order mechanics (registration order,
> pre-/post-`next()` split) are asserted backend-free by
> `tests/Affiant.Core.Tests/Services/ToolInvocationPipelineTests.cs`.
>
> **Correction (2026-08-03, area-3 P2 ruling 2).** From 2026-07-05 until this correction, this
> section's claim that steps 1–2 below (`ToolErrorFilter` outermost, `DeterministicShortCircuit`
> second) were "locked" by `AffiantFilterPipelineOrderTests` was **false**: `AddAffiantCore()`
> actually registered `DeterministicShortCircuit` *before* `ToolErrorFilter`, making
> `DeterministicShortCircuit` the true outermost filter, and the cited test never asserted the
> relative order of that specific pair (only that each preceded `ToolArgumentCaptureFilter`) — a
> documented guarantee that was neither true in code nor caught by the test that claimed to lock
> it. See `docs/architecture-review/area-3-tool-calling-reliability.md` (`affiant-chancery` review
> repo) V4 for the full finding. **Fixed**: `AddAffiantCore()` now registers `ToolErrorFilter`
> first; `AffiantFilterPipelineOrderTests.NeutralFilters_RegisteredInCanonicalOrder`
> (`tests/Affiant.SemanticKernel.Tests/Filters/AffiantFilterPipelineOrderTests.cs`) now asserts the
> **full** 7-filter chain (including `ToolTracingFilter`, previously untested in this diagram
> entirely — see the note below) as a single ordered sequence, not a subset of pairs, and is
> verified by self-mutation (swapping the two registrations back reproduces the original bug and
> fails the test) to actually catch this class of regression. The MAF bridge reproduces the
> identical order at its one middleware seam (no separate order-lock test is needed there for the
> *stage-split* concern because MAF has no stage split to get wrong — see
> `docs/adapters/microsoft-agent-framework.md` — but the `ToolErrorFilter`/`DeterministicShortCircuit`
> ordering bug above applied to MAF identically, since both backends share `AddAffiantCore()`; MAF
> needed no adapter-side fix, only the shared Core one).
>
> **`ToolTracingFilter` (undocumented step, area-3 V4/V7).** A third pre-tool filter,
> `ToolTracingFilter`, is registered by `AddAffiantCore()` between `DeterministicShortCircuit` and
> the host's `ContextExtractor` subclasses — it creates the `execute_tool` OTel span (§3.12.5) that
> wraps everything inward of it. It was previously invisible in this diagram; the diagram below now
> includes it. Consequence worth knowing: `ToolTracingFilter` sits *inside*
> `DeterministicShortCircuit`, so a short-circuited call (an `IIntentInterceptor` match) never gets
> an `execute_tool` span — this was true before the 2026-08-03 fix and remains true after it (moving
> `ToolErrorFilter` outermost did not change `ToolTracingFilter`'s position relative to
> `DeterministicShortCircuit`); a short-circuited call's *exceptions*, however, are now caught by
> `ToolErrorFilter` regardless (see the paragraph below).

The canonical 7-step filter pipeline order is expressed as an onion over the neutral
`IToolInvocationFilter` contract (§3.12.3); each backend's bridge decides which segment of the
onion fires at which of its framework's native seams:

```
Pre-tool (SK: IFunctionInvocationFilter · MAF: same middleware seam, earlier in the onion):
  1. ToolErrorFilter
  2. DeterministicShortCircuit
  2a. ToolTracingFilter (undocumented until 2026-08-03 — see the note above; sits between
      DeterministicShortCircuit and the host ContextExtractor subclasses)
  3. ContextExtractor* (host-registered)
  4. ToolArgumentCaptureFilter
  5. InferenceTriggerFilter
[tool invokes]
Post-tool (SK: IAutoFunctionInvocationFilter · MAF: same middleware seam, later in the onion):
  6. TaskInferenceMergeFilter
  7. ReviewGateFilter
```

**The step numbers above are *completion* order (the order in which each filter's work finishes), not onion *entry* order.** The distinction only matters for the two post-tool filters (6, 7): both do all their work *after* `await next()`, so on the onion unwind the filter entered *last* (innermost) runs its post-work *first*. Steps 1–5 do their work *before* `await next()`, so for them entry order and completion order coincide and the numbering reads directly as onion entry order.

Steps 4 and 5 are the L2 additions. `ToolArgumentCaptureFilter` (step 4) must precede `InferenceTriggerFilter` (step 5) so that captured arguments are available to `ITaskInferenceStrategy` implementations during inference. `TaskInferenceMergeFilter` (step 6) merges deferred inference results from the `ContextFabric` into the final Affidavit via `IAffidavitProjection`; its merge must **complete** before `ReviewGateFilter` (step 7) files the review, so the reviewer sees a fully-merged Affidavit. Because both are post-tool filters, achieving "merge completes before review files" requires `ReviewGateFilter` to be the **outer** (earlier-entered) of the two and `TaskInferenceMergeFilter` the **inner** (later-entered, so its post-work runs first on the unwind). This ordering is fixed in one place — `AddAffiantCompletionFilters()` in `Affiant.Core` — which both backends' registration calls, so the SK bridge and the MAF adapter cannot drift on it. On SK, steps 1–5 fire at `IFunctionInvocationFilter` and steps 6–7 at `IAutoFunctionInvocationFilter` — two native seams. On MAF, all seven fire at MAF's single function-calling middleware seam, in the same relative order, because MAF has no equivalent two-position split (§3.12.3). Steps 1–3 and 7 were part of the L1 pipeline; see §3.10 Task Inference Strategy and §7 Tool Authoring Guide for their documentation.

**SK completion-stage failure contract (2026-08-03, area-3 P2 ruling 1).** Before this fix, steps
6–7 above ran, on SK, in a *second, separate* `ToolInvocationPipeline.RunAsync` call
(`Affiant.SemanticKernel.Filters.BridgeStages.CompletionStage`) that contained only those two
filters — `ToolErrorFilter` was structurally absent from that call, so an exception surviving
either filter propagated raw into SK's own auto-invocation loop (able to fault the entire chat
turn, not just the one tool call), unlike MAF's single onion where `ToolErrorFilter` already
wrapped everything. `BridgeStages.CompletionStage` now also includes `ToolErrorFilter`, identified
structurally via the new `Affiant.Abstractions.Interfaces.ICompletionStageFilter` marker interface
(`TaskInferenceMergeFilter`/`ReviewGateFilter` implement it) rather than a closed type list, so a
third completion-stage filter added later inherits the same guarantee automatically. See §3.12.9
below for what "the same guarantee" actually resolves to for this class of failure — it is not
simply "convert to `ToolError`," and (fix round, 2026-08-03) not simply "never retried" either —
see §3.12.9's "Retry safety at the completion seam" for the `NextIsToolBody` mechanism that
actually governs it.

*Source files:* `src/Affiant.Core/Services/ToolInvocationPipeline.cs`, `src/Affiant.Core/Extensions/ServiceCollectionExtensions.cs` (`AddAffiantCompletionFilters`), `src/Affiant.SemanticKernel/Filters/AffiantFilterPipeline.cs`, `src/Affiant.SemanticKernel/Filters/BridgeStages.cs`, `src/Affiant.Core/Filters/InferenceTriggerFilter.cs`, `src/Affiant.Core/Filters/ToolArgumentCaptureFilter.cs`, `src/Affiant.Core/Filters/TaskInferenceMergeFilter.cs`, `src/Affiant.Core/Filters/ToolErrorFilter.cs`, `src/Affiant.AgentFramework/Filters/AffiantFunctionInvocationMiddleware.cs`, `src/Affiant.AgentFramework/Extensions/ServiceCollectionExtensions.cs`

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

*Source files:* `src/Affiant.Core/Observability/AffiantTelemetry.cs`, `src/Affiant.Abstractions/Models/AffidavitEmittedEvent.cs`, `src/Affiant.Core/Services/TaskInferenceRunner.cs`, `src/Affiant.Core/Services/SchemaDrivenAffidavitProjection.cs`

#### 3.12.6 Adopter Pattern

A host enabling L2 inference on a write tool needs three things:

1. **Decorate the `[KernelFunction]` method** with `[AffiantWriteTool]` (§3.11.4), specifying the operation kind, entity type, and strategy type — e.g., `[AffiantWriteTool("WriteCreate", "WorkOrder", typeof(WorkOrderCreateStrategy))]`.
2. **Implement `ITaskInferenceStrategy`** (§3.10 Task Inference Strategy). The strategy's `Fields` list declares which entity fields the framework should infer and their confidence thresholds. A typical 5-field entity strategy is approximately 30 lines.
3. **Call `AddAffiantInferenceOrchestration()`** once in DI setup. The extension registers all L2 components.

Optionally, a host may register `IDeterministicFieldSource` implementations for fields that have authoritative non-LLM sources (e.g., the authenticated user ID). These are registered with `services.AddSingleton<IDeterministicFieldSource, YourFieldSource>()` and are automatically picked up by `SchemaDrivenAffidavitProjection`.

The contrast with the pre-L2 pattern is significant: before L2, each write tool required approximately 350 lines across a per-tool inference filter, a per-tool form-data struct, a per-tool Affidavit mapper, and per-field provenance assignments — all host-maintained and all outside the framework's guarantees. With L2, the same coverage requires the `[AffiantWriteTool]` decoration, the strategy declaration (~30 lines), and the one-line DI registration.

*Source files:* `src/Affiant.SemanticKernel/Extensions/ServiceCollectionExtensions.cs`, `src/Affiant.Abstractions/Attributes/AffiantWriteToolAttribute.cs`

#### 3.12.7 Fail-Safe Semantics

Inference failure never breaks the agent turn. The fail-safe contract, enforced jointly by `InferenceTriggerFilter` and `TaskInferenceRunner`, is: any `Exception` other than `OperationCanceledException` thrown during inference is caught, an `inference.failed` span event is emitted with `affiant.error.kind` populated, a warning is logged at `LogWarning` level, and the tool call proceeds via `next(context)`. The agent receives a tool return and produces a non-null response.

`OperationCanceledException` is deliberately re-thrown — cancellation is user- or host-initiated and must propagate normally.

The fail-safe contract is asserted end-to-end by `InferenceFailSafeIntegrationTests` (`tests/Affiant.SemanticKernel.Tests/Integration/InferenceFailSafeIntegrationTests.cs`, landed in Story 16.6).

*Source files:* `src/Affiant.SemanticKernel/Filters/InferenceTriggerFilter.cs`, `src/Affiant.Core/Services/TaskInferenceRunner.cs`

#### 3.12.8 Idempotency Semantics

Inference runs at most once per `(ConversationId, FunctionName, TurnNumber)` tuple within a single agent turn, even when multiple `IInferenceTrigger` instances are registered and more than one returns `true`. Idempotency is enforced by `InferenceTriggerFilter` via a bookkeeping entity maintained in the `ContextFabric` under the reserved key `"inference_idempotency"`. When the filter evaluates a tool call and finds the tuple already marked, it skips inference and proceeds directly to `next(context)`.

Each bridge supplies `ConversationId` from its framework's neutral seam: the SK bridge reads
`kernel.Data["ConversationId"]` (and `TurnNumber` from `kernel.Data["AffiantTurnNumber"]`); the MAF
middleware reads `FunctionInvocationContext.Options.ConversationId` (the `ChatOptions` conversation id
the run carries) and `TurnNumber` from `FunctionInvocationContext.Iteration`. If `ConversationId` is
absent the filter falls back to a stable per-fabric-instance hash — conservative, and because the fabric
is now conversation-scoped (§3.12.3) that hash is itself per-conversation, so the fallback no longer
coalesces tuples across concurrent conversations the way a singleton fabric did.

The idempotency contract is asserted end-to-end by `InferenceIdempotencyIntegrationTests` (`tests/Affiant.SemanticKernel.Tests/Integration/InferenceIdempotencyIntegrationTests.cs`, landed in Story 16.6).

*Source files:* `src/Affiant.Core/Filters/InferenceTriggerFilter.cs`

#### 3.12.9 Tool-Body vs. Post-Processing Failure Policy

> Added 2026-08-03, area-3 P2 ruling 3. Fixes the finding at
> `docs/architecture-review/area-3-tool-calling-reliability.md` V5 (`affiant-chancery` review
> repo, written against commit `399c193`, before this fix): `ToolErrorFilter`'s retry-once wrapped
> the *entire remaining onion*, not just the tool call, so a bug in a post-tool filter (a host
> `ContextExtractor` subclass, or `TaskInferenceMergeFilter`) could discard a genuinely successful
> tool result and report failure to the model, or — if the exception happened to classify as
> retryable — cause the real tool to execute a second time for a failure that had nothing to do
> with the tool.

**The distinction.** Every neutral filter (§3.12.3) is either "tool body" or "post-processing"
relative to one tool call, not by filter *type* but by **when its own logic runs relative to the
real tool invocation**:

- **Tool body**: `DeterministicShortCircuit` (a pre-tool gate that decides whether the tool runs at
  all) and the actual tool invocation itself (the terminal delegate each bridge/middleware
  supplies). A failure here means the tool has not (yet) produced a result.
- **Post-processing**: any filter whose own logic runs strictly *after* the tool already returned
  a value — host `ContextExtractor` subclasses, `TaskInferenceMergeFilter`, `ReviewGateFilter`. A
  failure here occurs *after* a genuine tool result already exists.

**The mechanism.** `Affiant.Abstractions.Models.ToolInvocationContext.ToolExecuted` — a `bool`,
default `false` — is set to `true` by the bridge/middleware's terminal delegate the instant the
real tool call succeeds, **before** any post-processing filter's own logic runs (this ordering is
guaranteed by construction: post-processing filters call `await next(context)` first, and the
terminal — reached only once every filter between it and the caller has called `next`— is what
flips the flag). `ToolErrorFilter`'s catch clause branches on this flag:

- `ToolExecuted == false` when caught: a genuine tool-body failure (the tool has not succeeded on
  this attempt). Existing behavior, unchanged: map to a typed `ToolError`, retry once if the
  mapped code is classified retryable.
- `ToolExecuted == true` when caught: a post-processing failure — the tool already succeeded and
  `ToolInvocationContext.Result` already holds its genuine output. Per the gate ruling below,
  `ToolErrorFilter` **never touches `Result`, never retries** (a retry here would call `next()`
  again, re-executing the already-succeeded tool — exactly the V5 hazard), and only logs +
  emits the `affiant.extractor.failed` OTel event (tags: `extractor.type`, `tool.name`,
  `exception.type`) via `AffiantTelemetry.RecordExtractorFailedEvent`.

**Retry safety at the completion seam (fix round, 2026-08-03 — corrects a disproven claim).** The
`ToolExecuted == false` retry branch above was, until this fix round, additionally believed
"structurally impossible" to double-fire at SK's completion-stage seam. That claim was FALSE: two
independent adversarial refuters reproduced it. SK's completion-stage terminal
(`AffiantAutoFunctionInvocationBridge`)'s `next(context)` is SK's OWN auto-invocation continuation,
not the tool — it nested-invokes the real tool through a SEPARATE `ToolInvocationContext` at the
invocation-stage seam (§3.12.4). A real scenario this must guard against: a host-registered SK
filter running outside Affiant's bridges (or SK's own argument-binding step) throws before that
nested invocation ever happens. At that point `ToolExecuted` is still `false` (the tool never got a
chance to run), so the retry branch above — reading `ToolExecuted` alone — would call
`next(context)` a second time, calling SK's continuation again and genuinely re-executing the tool
for a failure that had nothing to do with it.

The fix: `Affiant.Abstractions.Models.ToolInvocationContext.NextIsToolBody` — a `bool`, default
`true` — declares whether re-invoking `next(context)` at a given seam re-runs ONLY the tool body.
`ToolErrorFilter`'s retry branch is gated on `!ToolExecuted && NextIsToolBody`; when a retryable
exception arrives with `ToolExecuted == false` and `NextIsToolBody == false`, it is still converted
to a typed `ToolError` (the one-failure-contract still holds — SK's auto-invoke loop never sees a
raw exception) but `next()` is never called again.
`AffiantAutoFunctionInvocationBridge` sets `NextIsToolBody = false` on the `ToolInvocationRequest`
it builds for the completion stage — the one seam where `next()` is not the tool body. MAF's single
onion (`AffiantFunctionInvocationMiddleware`) and `ManualToolInvoker`'s completion terminal both
leave it at the default `true`: at MAF's seam `next()` genuinely IS the tool, so retrying there is
correct, deliberate, and now explicitly tested (asymmetric with SK on purpose — see
`tests/Affiant.AgentFramework.Tests/Filters/CompletionSeamRetrySafetyTests.cs`, which promotes both
refuters' probes: the SK case asserts `next()` is called exactly once with no retry, and a MAF
control case asserts the retry still fires there, twice, because `next()` really is the tool). The
same class remarks in `ToolErrorFilter.cs` and `BridgeStages.cs` that carried the disproven
"structurally impossible"/"cannot double-fire" claims have been corrected to describe this
mechanism.

**Gate ruling — extraction policy = surface-and-continue (2026-08-03).** Extraction is enrichment,
not gating (see the Area 3 principle above): a fail-the-call policy for extractor bugs would mean
the framework lies to the model about a tool call that actually succeeded, and — when classified
retryable — silently double-executes the real tool. Under surface-and-continue, the tool result
stands, the `ContextFabric` misses one fact (recoverable in a later turn), and the loss is fully
operator-visible via the OTel event. `ContextExtractor` (its base class, `Affiant.Core.Filters`)
and `TaskInferenceMergeFilter` each additionally self-guard their own post-tool logic with the
identical catch-log-emit pattern (belt-and-suspenders with `ToolErrorFilter`'s `ToolExecuted`-gated
backstop above — see each type's own class remarks for why both layers exist); `ReviewGateFilter`
already self-guarded its own filing failure as of P1a (2026-08-03 morning, area-3 P1,
affiant#22/FV-9) — its filing-failure `ToolError` rewrite is a deliberate, documented exception to
"never report tool failure to the model" specifically because a lost `WriteProposal` genuinely is
the review gate itself failing, not enrichment failing (see `ReviewGateFilter`'s own class remarks
for the reasoning).

**Applies identically to both backends.** This entire mechanism lives in the neutral
`Affiant.Core`/`Affiant.Abstractions` layer — `ToolInvocationContext.ToolExecuted`,
`ToolErrorFilter`'s branch, `ContextExtractor`'s and `TaskInferenceMergeFilter`'s self-guarding —
so MAF's single onion and SK's two-stage split (§3.12.3, §3.12.4) both inherit it without any
adapter-specific code. Each bridge/middleware's terminal delegate sets `ToolExecuted = true` at its
own point of tool success: `AffiantFunctionInvocationBridge` and
`AffiantAutoFunctionInvocationBridge` (SK), `AffiantFunctionInvocationMiddleware` (MAF), and
`ManualToolInvoker` (which sets it unconditionally before its completion-stage pipeline call,
since its tool already ran via `kernel.InvokeAsync` beforehand).

Mutation-locked by `tests/Affiant.Core.Tests/Services/ToolBodyVsPostProcessingTests.cs` (counting
fake tool + throwing `ContextExtractor`: the tool runs exactly once, the model sees the genuine
result, the OTel event fires; a retryable tool failure: the tool runs exactly twice, the extractor
runs exactly once, on the final result) and by
`tests/Affiant.AgentFramework.Tests/Filters/CrossAdapterCompletionStageFailureContractTests.cs`
(the same injected completion-stage failure through both adapters' real bridges produces the
identical model-visible payload — the tool's untouched genuine result). The retry-safety fix
(`NextIsToolBody`) is mutation-locked by
`tests/Affiant.AgentFramework.Tests/Filters/CompletionSeamRetrySafetyTests.cs`: a real
`AffiantAutoFunctionInvocationBridge` with a `next()` that throws a retryable exception before the
tool runs calls `next()` exactly once (no retry, rewritten to a typed `ToolError`); the same
scenario through `AffiantFunctionInvocationMiddleware`'s single onion calls `next()` exactly twice
(the deliberate, documented asymmetry).

*Source files:* `src/Affiant.Abstractions/Models/ToolInvocationContext.cs`, `src/Affiant.Core/Filters/ToolErrorFilter.cs`, `src/Affiant.Core/Filters/ContextExtractor.cs`, `src/Affiant.Core/Filters/TaskInferenceMergeFilter.cs`, `src/Affiant.Core/Observability/AffiantTelemetry.cs`, `src/Affiant.SemanticKernel/Filters/AffiantAutoFunctionInvocationBridge.cs`, `src/Affiant.SemanticKernel/Filters/BridgeStages.cs`, `src/Affiant.Core/Services/ToolInvocationPipeline.cs`

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

**Responsibility**: Surfaces `GuidableElement` entries — keyed by semantic `ElementId`, never a CSS/DOM selector (Rule 6) — from the host's `IRouteRegistry` registration to downstream consumers (a plugin the LLM calls, a transport payload, a frontend renderer). The LLM discovers guidable elements through the registry, not by inspecting the DOM; translating an `ElementId` into a concrete DOM selector, if a host's rendering layer needs one, happens once, downstream of this layer, in the frontend renderer — never in the plugin or the transport payload it produces.

**Depends on**: Transport (Layer 1) for emitting `UiGuidance` events.

### Package Mapping

> Updated 2026-07-05: `Affiant.AgentFramework` joined the set as the ninth co-versioned package
> (`docs/proposals/affiant-maf-adapter.md`, implemented on branch `feat/agent-framework-adapter`).
> The `Affiant.SemanticKernel` entry below is also corrected — it previously described a single
> `IAutoFunctionInvocationFilter` implementation, which understated its shape even before this
> change (see §3.12.3 for the two-bridge-class reality).

The NuGet package hierarchy reflects the reserved `Affiant.*` namespace on nuget.org. Nine
packages exist today (the eighth published set plus `Affiant.AgentFramework`, in-repo pending the
maintainer's nuget.org ID reservation — see `docs/proposals/affiant-maf-adapter.md` §4.5 and §9):

**`Affiant.Abstractions`** — Bottom of the framework dependency graph: all domain-agnostic primitive types (`ToolEnvelope`, `Affidavit`, `ProvenanceTag`, `ProvenanceChain`, `DocketEntry`, `TransportEvent`, etc.) and all framework interfaces (`IChatSessionStore`, `IDocketStore`, `IStreamingTransport`, `IApprovalPolicy`, `IFieldMapper<T>`, `IWriteExecutor`, `IRouteRegistry`, `IIntentInterceptor`, `IToolInvocationFilter`, etc.). Zero dependencies on other Affiant packages, and — since the 2026-07-05 neutral-pipeline refactor — zero dependency on either `Microsoft.SemanticKernel` or `Microsoft.Agents.AI`. Host applications that only need to implement a contract can reference this package alone without pulling in `Core`'s services.

**`Affiant.Core`** — Concrete services built on top of `Affiant.Abstractions`: the `ContextFabric`, `ReviewGate`, `ContextExtractor<T>` base class, `TaskInferenceStep`, `DeterministicShortCircuit`, `UiGuidanceBridge`, `AffiantTelemetry`, the backend-neutral `ToolInvocationPipeline` and its `IToolInvocationFilter` implementations (§3.12.3), and the DI registration extension `AddAffiantCore()`. References `Affiant.Abstractions` only — no interception-backend package, per L2 AC #4.

**`Affiant.SemanticKernel`** — The Semantic Kernel interception bridge: `AffiantFunctionInvocationBridge` (SK `IFunctionInvocationFilter`) and `AffiantAutoFunctionInvocationBridge` (SK `IAutoFunctionInvocationFilter`) translate SK's two-position filter pipeline into the neutral `ToolInvocationPipeline` (§3.12.3) and back. Also: the connector-quirk abstraction (`IConnectorCapabilities`, `ManualToolInvoker`), provider configuration, and `SemanticKernelInferenceCompletionPort`.

**`Affiant.AgentFramework`** — The Microsoft Agent Framework (MAF) interception bridge, added 2026-07-05. `AffiantFunctionInvocationMiddleware` translates MAF's single function-calling middleware seam into the same neutral `ToolInvocationPipeline` SK uses. Also: `AffiantToolCatalog` (reflects a tool type into `AIFunction`s + `AffiantToolDescriptor`s in one pass), the `WithAffiant(...)` wiring extension, the hosted-tool coverage audit, and `AgentFrameworkInferenceCompletionPort`. See `docs/adapters/microsoft-agent-framework.md` for the host-facing guide.

**`Affiant.Docket`** — The durable review queue with persistence. Contains `IDocketStore` implementations and the `DocketExpiryService` background worker. Persistence adapters: PostgreSQL (via EF Core with `jsonb`), SQLite (for development), InMemory (for tests).

**`Affiant.EntityFramework`** — EF Core interceptor for the propose-review-commit pattern. The `IChatSessionStore` implementations (typed against the neutral `AffiantChatMessage`, §3.12.3), migration helpers, and the row-per-message schema.

**`Affiant.Policies`** — Standing Orders (auto-approval rules), Referral logic (escalation), risk-scoring functions, and the `IApprovalPolicy` evaluation pipeline.

**`Affiant.Transport.SignalR`** — SignalR adapter implementing `IStreamingTransport`. Reference transport for real-time Evidence Card delivery.

**`Affiant.Testing.ComplianceHarness`** — `ComplianceHarness.Verify(...)`, depending only on `Affiant.Abstractions`/`Affiant.Core` contracts (e.g. `IInferenceCompletionPort`), which is why a single compliance-parity `[Theory]` suite can run the same fixtures against both the SK and MAF interception backends (§3.12.3).

**Host** (application-specific, never in a package): Domain plugins, domain models, `IFieldMapper<T>` implementations, SignalR hub hosting, frontend application, authentication, authorization policies, system prompt content.

**Future packages** (Phase 3): `@affiant/react` (npm — React hook `useGuidance` + Evidence Card component), `Affiant.UiBridge.Blazor`.

---

## 5. Framework Boundary Contract

> Extended 2026-07-05 with Seam 4 (`docs/proposals/affiant-maf-adapter.md` §7). The boundary
> was a three-seam cut through 2026-06; the neutral tool-invocation pipeline (§3.12.3) made the
> interception backend itself a swappable seam rather than an SK-only implementation detail, so
> it is documented as a fourth seam here.

The boundary between framework and host is a four-seam cut. Everything below the seams is reusable. Everything above is host-specific.

**Seam 1 — Domain Plugins**: Host applications implement `[KernelFunction]`-decorated (SK) or reflected-`AIFunction` (MAF, via `AffiantToolCatalog.FromType<T>()`) methods that return `ReadResult` or `WriteProposal` via the `ToolEnvelope` contract. The framework never knows about aviation work orders, HR leave requests, or CRM contacts. It only knows about `ToolEnvelope<TResult>`.

**Seam 2 — Domain Models**: The framework's `Affidavit.Fields[]` carries `string` field names and `object` values. The host application's strongly-typed domain models live outside the framework. The `IFieldMapper<TDomainModel>` interface converts between the two.

**Seam 3 — Transport Configuration**: The SignalR hub, authentication middleware, and frontend application are host concerns. The framework provides `IStreamingTransport` and the reference `Affiant.Transport.SignalR` adapter. The host configures and hosts it.

**Seam 4 — Interception Backend**: The framework-specific seam where Affiant observes tool calls — argument capture before execution, result envelopes after, review gating on write intent — is itself a swappable adapter behind the neutral `IToolInvocationFilter`/`ToolInvocationPipeline` contract (§3.12.3). A host picks `Affiant.SemanticKernel` or `Affiant.AgentFramework` (or, in principle, a future third backend) without the framework's provenance, inference, or review-gate logic changing at all — that logic lives once, in `Affiant.Core`, on the neutral side of this seam. This seam did not exist as a documented boundary before 2026-07-05; it existed in practice (the SK bridge), but `Affiant.Core` and `Affiant.Abstractions` still leaked SK types across it until the neutral-pipeline refactor made the cut real.

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

> **`IRouteRegistry` is the single supported guidance model (corrected 2026-07-31).** A host registers each guidable element once, in one place, keyed by a stable `ElementId` (e.g. `HRPortalRouteRegistry`/`MeridianRouteRegistry` implementing `IRouteRegistry` — §3.7). Every layer above that registration — the `UiGuidancePlugin`-style tool the LLM calls, the transport payload it returns, and the frontend renderer that ultimately highlights the element — refers to the element **only by `ElementId`**. Nothing above the registration may hold, construct, or pass through a raw CSS/DOM selector string; a plugin field named `elementSelector` (or equivalent) is non-conformant regardless of who set its value. Selector-based UI guidance — where the LLM, a plugin, or a transport payload carries a `document.querySelector`-style string instead of an `ElementId` — is **unsupported**, not merely discouraged: it is the exact anti-pattern this rule exists to prevent, and there is no accepted fallback or transitional shape for it. The *frontend* renderer may still translate a registered `ElementId` into a concrete DOM selector (e.g. `` `[data-guide='${elementId}']` ``) as its own presentation-layer detail — that translation is UI-layer, happens once, after the `IRouteRegistry` lookup, and is not the thing this rule bans. What Rule 6 bans is selector authorship or transport above that translation point. This clarification was prompted by a 2026-07-31 audit of a reference host's `UiGuidancePlugin`, which built `[data-guide='...']` selector strings directly in the tool the LLM invoked — see the private `affiant-host-apps` repository, issue #9, for the host-side fix.

> **The wire path is now a framework mechanism, not host folklore (built 2026-08-04, area-4 architecture review P1f(b)).** Until 2026-08-04, this rule described a real discovery contract (`IRouteRegistry`) with no framework-owned way to actually deliver a guidance walkthrough to a client: the only implementation anywhere was a reference host hand-rolling a raw SignalR broadcast (`Clients.Group(...).SendAsync("GuideUI", ...)`) directly, bypassing the framework's transport abstraction entirely. As of 2026-08-04, `Affiant.Core.UiBridge.UiGuidanceBridge` carries the wire path itself: `BuildStep(elementId, description, prefillValue?, title?)` resolves a step's popover placement and highlight padding from the `GuidableElement.Attributes` registered for `elementId`, and `BroadcastGuidanceAsync(sessionGroupId, payload, ct)` sends the assembled `Affiant.Abstractions.Transport.UiGuidancePayload` through `IStreamingTransport` as `TransportEvent.UiGuidance` (wire method name `"GuideUI"` — preserved deliberately so an existing client keeps working unmodified; see §2.10 and §3.1). What stays host-owned is exactly what Rule 6 was always about: per-step *content* (which fields to guide through, prefill values, description text) is domain-specific and is composed by the host's own guidance tool before being handed to `UiGuidanceBridge` — the framework now carries the mechanism, never the content, mirroring `ReviewGate`'s own split between framework-owned filing and host-owned review context (§6 Rule 3, §4 Layer 4).

**Rule 7: Every Affidavit field carries provenance, no exceptions.** If a field's provenance is unknown, it must be tagged `ProvenanceSource.Empty` — never omitted. The Evidence Card renders provenance as visual indicators (green for UserStated, amber for Inferred, grey for Default). *Rationale*: Missing provenance is indistinguishable from "the framework forgot to track it" versus "the AI made this up." *Anti-pattern*: Fields without provenance tags that the UI renders identically to user-confirmed values.

> **The Area 3 gating principle (ratified 2026-08-03, verbatim from the ecosystem architecture
> review's Area 3 position paper — `docs/architecture-review/area-3-tool-calling-reliability.md`
> in the `affiant-chancery` review repo).** This is not an eighth normative rule with its own
> number — it is the load-bearing generalization Rules 3, 4, and 7 above already imply, made
> explicit because the review found the framework violating it in three separate places
> (§3.12.4's filter-order drift, §3.12.9's retry/extraction hazard, and a P1-fixed silent
> `ReviewGateFilter` failure — see the CHANGELOG's "Area 3" entries):
>
> *"Anything that gates a write must be pipeline-guaranteed: a deterministic check in the tool or
> an intent interceptor in the pipeline. Tool-choice steering (prompts, ToolMode forcing) is UX
> optimization — it may improve the path, it must never be load-bearing for safety. And every
> failure on the gated path must surface — to the model as a typed ToolError, to the user as a
> visible state, to the operator as telemetry. No silent branch anywhere between 'model proposed'
> and 'human decided.'"*
>
> **Glossary for readers without the review's context:** *"gates a write"* = anything standing
> between the model proposing a write (calling a `WriteCreate`/`WriteUpdate` tool, §3.11.1) and a
> human actually confirming it via `ReviewGate` (§2.7, §4 Layer 4) — e.g. a redirect that refuses
> to call the write tool at all when a precondition is unmet. *"Deterministic check in the tool"*
> = an in-tool guard the tool itself always runs, regardless of what the model intended (a plain
> `if` statement before the tool returns a `WriteProposal`). *"Intent interceptor in the pipeline"*
> = `IIntentInterceptor`/`DeterministicShortCircuit` (§3.8) — a pipeline-level guard that runs
> before the model's tool call is even executed. *"ToolMode forcing"* = telling the LLM provider
> it MUST call a specific tool on its next turn (a provider API feature; not a framework
> primitive — no `Affiant.*` package implements or wraps it as of this writing). *"Load-bearing
> for safety"* = a mechanism whose failure would let an unsafe write through; UX mechanisms may
> fail without consequence to safety, only to convenience/speed.
>
> **Field-evidence status of ToolMode forcing (as of 2026-08-03):** this repository contains no
> `ToolMode`/tool-choice-forcing code or documentation — the pilot referenced above
> (`Affiant:LookupToolModeEnforcement`) is entirely host-side (the private `affiant-host-apps`
> repository's Meridian host), not a framework primitive, and this document cannot make claims
> about host-side test/trace evidence it does not contain. The framework-side implication of this
> ruling is narrower and fully covered by the code in this repository: `DeterministicShortCircuit`/
> `IIntentInterceptor` (§3.8) is the one pipeline-level gating primitive the framework ships, and
> per this principle it — not any prompt or tool-choice API — is what a host should reach for when
> a write must be gated deterministically. See the private `affiant-host-apps` repository's own
> documentation for that repo's field-evidence status on ToolMode forcing specifically.

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
    CreatedAt, ExpiresAt, Status (Pending|Approved|Rejected|Expired|Deferred)

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
