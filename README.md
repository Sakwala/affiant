# Affiant

**Sworn provenance for every AI write.**

An *affiant* is one who swears to the truth of a statement. Affiant is a deterministic
evidence layer for .NET agents: every field an AI proposes to write to your database
carries a sworn record of *where that value came from* — user-stated, looked up from an
external system, computed by your own logic, or merely inferred by the model — and nothing
reaches the database until a human has seen that evidence and approved it.

Nothing commits without evidence. Nothing writes without approval.

> **See it live.** Two first-party hosts built on Affiant run in public: [Meridian](https://meridian.affiant.dev),
> an aircraft-maintenance desk on the Microsoft Agent Framework, and [HR Portal](https://hrportal.affiant.dev),
> an HR department on Microsoft.Extensions.AI. Any email works, five messages a day, and the data resets
> nightly. What to try and what you are looking at:
> [affiant.dev/start/live-demo](https://affiant.dev/start/live-demo/).

> **Beta.** `1.0.0-beta.1` is on nuget.org — all ten packages, co-versioned, published 2026-08-23 (UTC).
> The public API has been validated by two first-party host applications but has not yet reached 1.0 GA.
> Read [Beta status](#beta-status) before adopting — trust the *invariant*, expect the *API* to evolve.

---

## Why this exists

Whole-call tool approval is now commodity: Microsoft Agent Framework 1.0 ships built-in
approval for AI function calls, gating a call by its name and a raw arguments blob.

Affiant does something approval-gating cannot. It tracks provenance **per field**, not per
call:

- Every proposed value carries a **`ProvenanceSource`** — a seven-state taxonomy from
  `UserStated` (the user said it) down to `Empty` (provenance unknown, and *explicitly
  marked* as such).
- Every value carries a **`ProvenanceChain`** — the ordered history of how that field
  arrived at its current value — and its **`PreviousValue`**, so an update shows exactly
  what is changing.
- Every proposed mutation is a durable, sworn **`Affidavit`** carrying three confidence
  numbers, reviewed on an **Evidence Card** and persisted for audit whether it is approved or
  rejected. **`AggregateConfidence`** is the *minimum* over every proposed field with an
  `Empty` field counting as `0` — so it is `0` exactly when some proposed field has unknown
  provenance, and a mostly-blank record can never report a high score.
  **`PopulatedConfidence`** (the minimum over the fields that *are* populated, `null` when
  none is) and **`EmptyFieldCount`** are what make that `0` readable.

The result is *data identity*: not "an agent did something", but "this specific value came
from this specific source, and here is the proof". See [Positioning](#positioning) for how
this complements — rather than competes with — Microsoft's agent-governance stack.

---

## What a write looks like

A user asks the agent to do something. The write tool does **not** touch the database — it
returns a `WriteProposal` wrapping an `Affidavit`, one field at a time, each with its own
provenance:

```jsonc
{
  "$type": "write",
  "toolName": "RequestLeave",
  "timestamp": "2026-07-04T09:24:11Z",
  "envelope": {
    "operationType": "create",
    "entityType": "LeaveRequest",
    "entityId": null,                     // null = create; non-null = update
    "fields": [
      {
        "name": "StartDate",
        "value": "2026-07-20",
        "previousValue": null,
        "provenance": {
          "current": { "source": "UserStated", "confidence": 1.0,
                       "evidence": "User stated: StartDate", "conversationTurn": 3 },
          "prior": []
        }
      },
      {
        "name": "EmployeeId",
        "value": 4021,
        "previousValue": null,
        "provenance": {
          "current": { "source": "External", "confidence": 0.95,
                       "evidence": "Resolved from directory lookup", "conversationTurn": 2 },
          "prior": []
        }
      },
      {
        "name": "RemainingDaysAfter",
        "value": 12,
        "previousValue": null,
        "provenance": {
          "current": { "source": "Computed", "confidence": 1.0,
                       "evidence": "currentBalance(17) - requestedDays(5)", "conversationTurn": 3 },
          "prior": []
        }
      }
    ],
    "aggregateConfidence": 0.98,
    "warnings": [],
    "requiresConfirmation": true
  }
}
```

That `Affidavit` is filed on the **Docket** (the durable review queue), rendered as an
**Evidence Card** for a human, and only after approval does the framework call your
`IWriteExecutor` to perform the actual database write. The flow, end to end:

```
user message
   → write tool returns WriteProposal(Affidavit)      // tools never write
   → ReviewGate files it on the Docket
   → Evidence Card shown to a human reviewer
   → reviewer approves (optionally amends fields)
   → IWriteExecutor persists the approved Affidavit    // the only place SaveChanges happens
```

---

## The determinism hierarchy

`ProvenanceSource` is ordered from most deterministic to least. The ordering is
load-bearing: when two provenance tags carry equal confidence, the merge rule breaks the tie
in favour of the lower ordinal (the more deterministic source).

| Source | Meaning |
|--------|---------|
| `UserStated` | The user explicitly stated this value. Maximal confidence. |
| `External` | Fetched from an authoritative external system (API, database read, third-party service). |
| `Computed` | Derived by deterministic business logic (tax, date math, SLA computation). |
| `Conversation` | Surfaced through a tool result in context, but not stated as a value by the user. |
| `Inferred` | Inferred by the LLM from conversational signals. Requires reviewer confirmation. |
| `Default` | System default or fallback applied when no conversational basis exists. |
| `Empty` | Provenance unknown — and **explicitly tagged**, never omitted. Missing provenance must be indistinguishable from nothing; a value with no basis is the loudest state on the card, not a silent blank. |

The final rule of the framework is absolute: **every Affidavit field carries provenance, no
exceptions.** If the source is unknown, it is tagged `Empty` — it is never left off.

---

## Quickstart

Affiant has three interception backends — Semantic Kernel (SK), Microsoft Agent Framework
(MAF), and Microsoft.Extensions.AI (M.E.AI) — sitting on one shared, backend-neutral pipeline in
`Affiant.Core`. Pick the one your host already uses; all three walkthroughs below end at the same
place, a filed `Affidavit` visible as a rendered Evidence Card (§"See your first Evidence Card" in
each). If you are choosing among the three for a new host: MAF is Microsoft's current-generation
agent SDK (GA 2026-04-03); M.E.AI is the lower-level abstraction both SK and MAF sit on top of —
pick it if your host talks to `IChatClient` directly and doesn't want an agent-framework dependency
at all; SK remains fully supported and is what the reference example throughout this README uses.

See ["Which of the 10 packages do I need?"](#which-of-the-10-packages-do-i-need) for the install
list per scenario, and ["Mandatory vs optional wiring"](#mandatory-vs-optional-wiring) for what
happens if you skip a call — some gaps now fail loudly at startup, some don't yet.

### Semantic Kernel

**1. Register the framework** (`Affiant.Core` + the Semantic Kernel adapter):

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantSemanticKernel();
builder.Services.AddAffiantInferenceOrchestration();
```

`AddAffiantInferenceOrchestration()` is what powers the `Inferred` row of the
[determinism hierarchy](#the-determinism-hierarchy) above — it wires the pre-tool filters that
ask the model for structured output on fields your tool call didn't receive directly, so an
`Affidavit` field can honestly carry `ProvenanceSource.Inferred` instead of being silently
absent. **Required if any write tool leaves a field for the model to infer rather than always
receiving it as a typed argument or looking it up itself; skip it only if every field on every
write tool always arrives as `UserStated`, `External`, or `Computed`** — the worked example
below is exactly that simpler case (every field comes straight off the tool call's parameters),
so it would run without this line, but almost every real host needs it and the line is shown
here so you don't discover it's missing from a cold read of this README, which is what
happened before this section was rewritten (see the CHANGELOG's Area-8 entries).

**2. Declare an inference strategy** — the field schema for one writable entity. This is the
domain-specific contract the framework uses to request structured output from the model and
to merge inferred values:

```csharp
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

public sealed class LeaveTaskInferenceStrategy : ITaskInferenceStrategy
{
    public string EntityName => "LeaveRequest";
    public double? MinimumConfidenceThreshold => 0.5;

    public IReadOnlyList<TaskInferenceField> Fields { get; } = new List<TaskInferenceField>
    {
        new("StartDate", "string", "Start date (yyyy-MM-dd)",
            Pattern: @"^\d{4}-\d{2}-\d{2}$", Required: true),
        new("EndDate", "string", "End date (yyyy-MM-dd), inclusive",
            Pattern: @"^\d{4}-\d{2}-\d{2}$", Required: true),
        new("LeaveType", "string", "Type of leave",
            Enum: new[] { "Annual", "Sick", "Personal" }, Required: true),
        new("Reason", "string", "Reason for the request", MaxLength: 1000, Required: true),
    };
}
```

**3. Write the tool** — mark write intent at the tool site with `[AffiantWriteTool]`, and
return a `ToolEnvelope` (here a `WriteProposal`). The tool builds the `Affidavit`; it does
not write to the database:

```csharp
using System.ComponentModel;
using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Models;
using Microsoft.SemanticKernel;

public class RequestLeavePlugin(HrDbContext db)
{
    [KernelFunction("request_leave")]
    [AffiantWriteTool("WriteCreate", "LeaveRequest", typeof(LeaveTaskInferenceStrategy))]
    [Description("Propose a leave request. Returns a WriteProposal for review; never writes directly.")]
    public Task<string> RequestLeaveAsync(
        [Description("Start date (yyyy-MM-dd).")] DateOnly startDate,
        [Description("End date (yyyy-MM-dd), inclusive.")] DateOnly endDate,
        [Description("Annual, Sick, or Personal.")] string leaveType,
        [Description("Reason for the request.")] string reason)
    {
        const string toolName = "RequestLeave";

        var fields = new AffidavitField[]
        {
            // The user typed these into the chat, so the binding is the form-style input they
            // came from. Pass null only when there is genuinely nothing to point at.
            new("StartDate", startDate.ToString("yyyy-MM-dd"), null,
                ProvenanceChain.From(ProvenanceTag.FromUser(
                    "StartDate", new ProvenanceBinding.FormInput(new FormInputRef("startDate"))))),
            new("EndDate", endDate.ToString("yyyy-MM-dd"), null,
                ProvenanceChain.From(ProvenanceTag.FromUser(
                    "EndDate", new ProvenanceBinding.FormInput(new FormInputRef("endDate"))))),
            new("LeaveType", leaveType, null,
                ProvenanceChain.From(ProvenanceTag.FromUser(
                    "LeaveType", new ProvenanceBinding.FormInput(new FormInputRef("leaveType"))))),
            new("Reason", reason, null,
                ProvenanceChain.From(ProvenanceTag.FromUser(
                    "Reason", new ProvenanceBinding.FormInput(new FormInputRef("reason"))))),
        };

        // Affidavit.Create computes all three confidence numbers from the fields, so a
        // hand-written aggregate can never disagree with what it is meant to summarise.
        var affidavit = Affidavit.Create(
            operationType: "create",
            entityType: "LeaveRequest",
            entityId: null,
            fields: fields,
            warnings: []);

        return Task.FromResult(
            new WriteProposal(toolName, DateTimeOffset.UtcNow, affidavit).ToJsonString());
    }
}
```

**4. Register the tool** so the framework knows its operation, entity type, and strategy:

```csharp
builder.Services.AddAffiantTool<LeaveTaskInferenceStrategy>(
    functionName: "request_leave", operation: Operation.WriteCreate, entityType: "LeaveRequest");
```

**5. Complete the wiring** — persistence, policy, transport. You also still need an
`IFieldMapper<T>` (Affidavit ↔ your domain model, host-authored) and an `IWriteExecutor` (the
one place `SaveChanges` runs, also host-authored) — both are covered in
[`docs/tool-authoring-guide.md`](docs/tool-authoring-guide.md), not shown here since they are
domain-specific. What every host needs regardless of domain:

```csharp
// Persistence — pick ONE store for IDocketStore + IChatSessionStore, but AddAffiantDocket()
// is needed either way, for the backend-neutral expiry sweep:
builder.Services.AddAffiantEntityFramework(ef => ef.UseSqlite(connectionString)); // durable
// -- or, for a process-local store with nothing surviving a restart (dev/test only) --
// builder.Services.AddAffiantDocket(d => d.UseInMemory());
builder.Services.AddAffiantDocket();

// Policy graph — governs auto-approval / escalation / reviewer-confirmation routing.
// Omit this and every write falls through to the built-in ReviewerConfirmation default.
builder.Services.AddAffiantPolicies();

// Transport — SignalR is the only shipped IStreamingTransport. THub is host-authored,
// subclassing AffiantHub.
builder.Services.AddAffiantSignalR<MyChatHub>();
```

```csharp
// In the request pipeline section, after app.Build():
app.MapAffiantSignalR<MyChatHub>();
```

Read tools follow the same shape as the write tool above and return a `ReadResult` instead of a
`WriteProposal`. The full walkthrough — read tools, context extraction, field mapping, error
handling, and testing — is in [`docs/tool-authoring-guide.md`](docs/tool-authoring-guide.md). If
you are also registering your own filters alongside these calls, read
[`docs/di-registration-order.md`](docs/di-registration-order.md) first — two registration-order
rules exist that no startup check catches yet.

**6. See your first Evidence Card.** Affiant ships no bundled UI — "rendered" here means the
framework has done its job: `ReviewGate` filed the `Affidavit` on the Docket and pushed an
`EvidenceCardRequest` down the transport to whichever client is in the reviewer's SignalR group.
Concretely, once a client is connected and in that group, this is the payload it receives, over
the wire, unprompted:

```jsonc
// Client-side: connection.on("ConfirmAction", payload => { /* render it */ });
// TransportEvent.EvidenceCardRequest's wire name is "ConfirmAction" (see TransportEventExtensions).
{
  "docketId": "5b1e8f2a-...-9c3d",
  "affidavit": { /* the WriteProposal's Affidavit — see "What a write looks like" above */ },
  "requiredBy": "2026-08-20T09:39:11Z",
  "priorAmendments": null
}
```

Turning that payload into an on-screen card — the actual `<affiant-evidence-card>`-shaped UI a
human reviewer clicks Approve/Reject on — is host territory; neither reference host application
ships a bundled or reusable web component for it as of this release (each renders its own). What
this Quickstart gets you to is the point where that payload exists and is on the wire, which is
everything the framework itself is responsible for. From there, `EvidenceCardResponse` (the
reviewer's decision, including any field amendments) travels back the same transport and
`ReviewGate.HandleDecisionAsync` picks it up — with a `DecisionContext` naming the principal the host
authenticated and the tenant they are acting in, which is what the gate holds them to and what the
row records as the attestation. Who may decide a given entry is the host's own answer, supplied once
through `services.AddDecisionAuthorization<TPolicy>()`; without one the gate refuses every decision
and the application is refused at startup.

Install:

```bash
dotnet add package Affiant.Core --prerelease
dotnet add package Affiant.SemanticKernel --prerelease
dotnet add package Affiant.EntityFramework --prerelease  # or Affiant.Docket alone, in-memory only
dotnet add package Affiant.Docket --prerelease            # always — the expiry sweep
dotnet add package Affiant.Policies --prerelease
dotnet add package Affiant.Transport.SignalR --prerelease
```

### Microsoft Agent Framework

The MAF adapter wires the same neutral pipeline through MAF's function-calling middleware
instead of SK's filter pipeline. Full reference:
[`docs/adapters/microsoft-agent-framework.md`](docs/adapters/microsoft-agent-framework.md).

**1. Register the framework:**

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantAgentFramework();
```

Unlike the SK adapter, there is no separate `AddAffiantInferenceOrchestration()` call for MAF —
`AddAffiantAgentFramework()` registers the equivalent inference-orchestration stack in the same
call (see the package's own XML `<summary>` on `AddAffiantAgentFramework` for the exact
one-call-covers-both-of-SK's-two-calls mapping).

**2. Declare an inference strategy and write the tool** — identical shape to the SK example
above (`ITaskInferenceStrategy`, `[AffiantWriteTool]`), because both backends read the same
`Affiant.Abstractions` contracts. MAF reflects over a tool *type*, not per-method attributes for
discovery, and has no `[KernelFunction]`-equivalent marker — every public instance method
becomes a callable tool, so a MAF tool type's public surface should contain only tool methods:

```csharp
public sealed class LeaveTools(HrDbContext db)
{
    [AffiantWriteTool("WriteCreate", "LeaveRequest", typeof(LeaveTaskInferenceStrategy))]
    [Description("Propose a leave request. Returns a WriteProposal for review; never writes directly.")]
    public Task<string> RequestLeaveAsync(DateOnly startDate, DateOnly endDate, string leaveType, string reason)
    {
        // Same body as the SK example: build the Affidavit's fields with their ProvenanceChain,
        // wrap it in a WriteProposal, never touch the database directly.
        // ...
    }
}
```

**3. Build the catalog and wrap the agent:**

```csharp
var catalog = AffiantToolCatalog.FromType<LeaveTools>();
builder.Services.AddScoped<LeaveTools>();

// ... after the service provider and IChatClient exist:
AIAgent agent = new ChatClientAgent(
        chatClient, instructions: "...", tools: catalog.Functions.Cast<AITool>().ToList(),
        services: serviceProvider)
    .WithAffiant(serviceProvider, catalog);
```

`WithAffiant(...)` is the only supported way to attach Affiant to an `AIAgent` — it also runs the
hosted-tool coverage audit (refuses at wire-up time if the wrapped agent carries a provider-side
tool, such as a hosted web-search tool, that MAF's client-side middleware can't see and Affiant
therefore cannot swear to; see `AgentFrameworkOptions.AcknowledgeUncoveredTools` to acknowledge
one deliberately). **Wrapping produces a new `AIAgent` instance — the pre-wrap `agent` local
silently bypasses Affiant if anything in your codebase calls it instead of the wrapped return
value.**

**4. Complete the wiring, and 5. see your first Evidence Card** — identical to steps 5 and 6 of
the SK quickstart above: same `AddAffiantEntityFramework`/`AddAffiantDocket`/`AddAffiantPolicies`/
`AddAffiantSignalR` calls, same `EvidenceCardRequest` payload over the same transport, because
both backends terminate in the same backend-neutral `ReviewGate`.

Install:

```bash
dotnet add package Affiant.Core --prerelease
dotnet add package Affiant.AgentFramework --prerelease
dotnet add package Affiant.EntityFramework --prerelease  # or Affiant.Docket alone, in-memory only
dotnet add package Affiant.Docket --prerelease            # always — the expiry sweep
dotnet add package Affiant.Policies --prerelease
dotnet add package Affiant.Transport.SignalR --prerelease
```

### Microsoft.Extensions.AI

The M.E.AI adapter wires the same neutral pipeline through Microsoft.Extensions.AI's own
function-calling seam — no agent framework required. Pick this if your host talks to `IChatClient`
directly. `Affiant.SemanticKernel` and `Affiant.AgentFramework` both sit on top of
Microsoft.Extensions.AI internally; this adapter targets that shared layer directly instead.

**1. Register the framework:**

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantExtensionsAI();
```

Like the MAF adapter and unlike SK, there is no separate `AddAffiantInferenceOrchestration()` call
— `AddAffiantExtensionsAI()` registers the equivalent inference-orchestration stack in the same
call.

**2. Declare an inference strategy and write the tool** — identical shape to the SK and MAF
examples above (`ITaskInferenceStrategy`, `[AffiantWriteTool]`), because all three backends read the
same `Affiant.Abstractions` contracts. Like MAF, this adapter reflects over a tool *type*, not
per-method attributes for discovery — every public instance method becomes a callable tool:

```csharp
public sealed class LeaveTools(HrDbContext db)
{
    [AffiantWriteTool("WriteCreate", "LeaveRequest", typeof(LeaveTaskInferenceStrategy))]
    [Description("Propose a leave request. Returns a WriteProposal for review; never writes directly.")]
    public Task<string> RequestLeaveAsync(DateOnly startDate, DateOnly endDate, string leaveType, string reason)
    {
        // Same body as the SK/MAF examples: build the Affidavit's fields with their ProvenanceChain,
        // wrap it in a WriteProposal, never touch the database directly.
        // ...
    }
}
```

**3. Build the catalog and wire `ChatOptions`:**

```csharp
var catalog = AffiantToolCatalog.FromType<LeaveTools>();
builder.Services.AddScoped<LeaveTools>();

// The chat client must have UseFunctionInvocation() — that is the client that runs the tool loop
// and publishes the per-call FunctionInvokingChatClient.CurrentContext Affiant's wrapper reads.
IChatClient client = new ChatClientBuilder(innerClient)
    .UseFunctionInvocation()
    .Build(serviceProvider);

var chatOptions = new ChatOptions { Tools = [.. catalog.Functions] }
    .WithAffiant(serviceProvider, catalog);

var response = await client.GetResponseAsync(messages, chatOptions);
```

`WithAffiant(...)` is the only supported way to attach Affiant here — the `ChatOptions`-extension
counterpart of MAF's `AIAgent.WithAffiant`. It also runs the hosted-tool coverage audit (refuses at
wire-up if the tool list carries a provider-side tool, such as a hosted web-search tool, that this
adapter's client-side wrapper can't see; see `ExtensionsAIOptions.AcknowledgeUncoveredTools` to
acknowledge one deliberately). **Wrapping returns a new `ChatOptions` instance — the pre-wrap
`chatOptions` local (or whatever object you built before calling `WithAffiant`) silently bypasses
Affiant if anything in your codebase uses it instead of the returned value.**

**4. Complete the wiring, and 5. see your first Evidence Card** — identical to steps 5 and 6 of the
SK quickstart above: same `AddAffiantEntityFramework`/`AddAffiantDocket`/`AddAffiantPolicies`/
`AddAffiantSignalR` calls, same `EvidenceCardRequest` payload over the same transport, because all
three backends terminate in the same backend-neutral `ReviewGate`.

**One Affiant adapter per tool catalog.** Never wire both `Affiant.Extensions.AI` and
`Affiant.AgentFramework` over the same tool catalog or chat-client pipeline — the neutral pipeline is
not idempotent (double-tagged provenance, task inference fired twice, the same write proposal filed
on the docket twice), and unlike the same-adapter double-wrap above, this specific cross-adapter case
cannot be detected: MAF rewrites `ChatOptions.Tools` with its own private wrapper type after this
adapter's wire-up has already run, and that type carries no marker either package can see.

Install:

```bash
dotnet add package Affiant.Core --prerelease
dotnet add package Affiant.Extensions.AI --prerelease
dotnet add package Affiant.EntityFramework --prerelease  # or Affiant.Docket alone, in-memory only
dotnet add package Affiant.Docket --prerelease            # always — the expiry sweep
dotnet add package Affiant.Policies --prerelease
dotnet add package Affiant.Transport.SignalR --prerelease
```

---

## Which of the 10 packages do I need?

Affiant has no meta-package — ten granular packages, install only what your scenario needs
(the same shape as `Microsoft.Extensions.*` or Duende IdentityServer's core-plus-adapters split).

| Your situation | Install |
|---|---|
| Implementing a contract only (e.g. your own `IDocketStore`), no runtime wiring | `Affiant.Abstractions` |
| Semantic Kernel host, in-memory/dev, nothing survives a restart | `Affiant.Abstractions`, `Affiant.Core`, `Affiant.SemanticKernel`, `Affiant.Docket`, `Affiant.Policies`, `Affiant.Transport.SignalR` |
| Semantic Kernel host, SQL-backed (SQLite/PostgreSQL) production | same six, **+ `Affiant.EntityFramework`** (installed alongside `Affiant.Docket`, not instead of it — see the quickstart above) |
| Microsoft Agent Framework host, in-memory/dev | `Affiant.Abstractions`, `Affiant.Core`, `Affiant.AgentFramework`, `Affiant.Docket`, `Affiant.Policies`, `Affiant.Transport.SignalR` |
| Microsoft Agent Framework host, SQL-backed production | same six, **+ `Affiant.EntityFramework`** |
| Microsoft.Extensions.AI host (no agent framework), in-memory/dev | `Affiant.Abstractions`, `Affiant.Core`, `Affiant.Extensions.AI`, `Affiant.Docket`, `Affiant.Policies`, `Affiant.Transport.SignalR` |
| Microsoft.Extensions.AI host, SQL-backed production | same six, **+ `Affiant.EntityFramework`** |
| Any backend, proving your own write strategies carry real provenance in CI | add `Affiant.Testing.ComplianceHarness` to your **test** project only |

## Mandatory vs optional wiring

No single `Add*` call wires everything — reconstructed here in one table instead of scattered
across throw sites and XML `<remarks>`. "Enforced?" states what happens **today** (`1.0.0-beta.1`)
if you skip a required call; some gaps now fail loudly at startup, some remain silent until first
use, and DI **ordering** mistakes (as opposed to missing registrations) are covered separately in
[`docs/di-registration-order.md`](docs/di-registration-order.md), not this table.

| Call | Required? | If skipped, enforced how? |
|---|---|---|
| `AddAffiantCore()` | Always, first | `AddAffiantTool<T>`/`AddAffiantReadTool` throw at registration time naming the fix |
| `AddAffiantTool<TStrategy>()` / `AddAffiantReadTool()` | Per tool | N/A — this call *is* the registration |
| `AddAffiantSemanticKernel()` | SK hosts only | `AffiantStartupValidator` throws at startup if a `[KernelFunction]` has no matching descriptor, or a registered inference strategy can't resolve from DI |
| `AddAffiantInferenceOrchestration()` (SK) | Only if any write tool leaves a field for the model to infer (see the [Quickstart](#semantic-kernel)) | **Not enforced.** No validator checks its presence; a missing call means inference silently never fires, not an error |
| `AddAffiantAgentFramework()` + `WithAffiant(...)` (on `AIAgent`) | MAF hosts only | The hosted-tool coverage audit throws at `WithAffiant()` call time if a hosted/provider-side tool is uncovered and unacknowledged |
| `AddAffiantExtensionsAI()` + `WithAffiant(...)` (on `ChatOptions`) | M.E.AI hosts only | Same hosted-tool coverage audit, thrown at `WithAffiant()` call time; also refuses at wire-up if the tool list is already wrapped by this adapter (double-wrap guard) |
| `AddAffiantDocket()` | Always — supplies the backend-neutral expiry sweep even when the store itself comes from `Affiant.EntityFramework` | **Enforced since `1.0.0-beta.1` (area-8 ruling 6):** `AddAffiantCore()`'s `AffiantWireUpValidator` throws at startup if no package registered an `IDocketStore` at all, naming both ways to supply one |
| `AddAffiantEntityFramework()` | Only if SQL-backed persistence is wanted (in addition to, not instead of, `AddAffiantDocket()`) | Same `AffiantWireUpValidator` check as above — it doesn't care which package supplied `IDocketStore`, only that one did |
| `AddAffiantPolicies()` | Recommended, not throw-enforced | Skipped: every write falls through to the built-in `ReviewerConfirmation` default policy — a working, if unopinionated, fallback, not a startup failure. One thing inside it *is* enforced: a Standing Order that declares a `RiskThreshold` throws on its first evaluation, before any write is auto-approved, unless `SetRiskScoreCalculator<T>()` supplied a calculator — `AffiantPolicies.ValidateStandingOrders(app.Services)` runs the same check at startup |
| `AddAffiantSignalR<THub>()` + `app.MapAffiantSignalR<THub>()` | Always (the only shipped transport) | **Enforced since `1.0.0-beta.1`:** the same `AffiantWireUpValidator` throws at startup if no package registered `IStreamingTransport`, naming the fix |
| Host filter registration order relative to `AddAffiantCore()`/`AddAffiantSemanticKernel()` | Two specific orderings, if you register your own filters | **Not enforced.** See [`docs/di-registration-order.md`](docs/di-registration-order.md) |

`AffiantCoreOptions.AcknowledgeMissingReviewWiring = true` downgrades the `AffiantWireUpValidator`
throw to a startup warning per missing contract, for a host deliberately running Affiant's
read/inference half with no review loop.

---

## The packages

Ten co-versioned packages target `net10.0` (`Affiant.AgentFramework` joined the set 2026-07-05,
`Affiant.Extensions.AI` joined 2026-08-20; both packages published on nuget.org with the rest,
see [Beta status](#beta-status)). The dependency graph is a strict DAG rooted at
`Affiant.Abstractions`, mirroring the `Microsoft.Extensions.*.Abstractions` / `Microsoft.Extensions.*`
convention — depend only on what you need.

| Package | Purpose | Depends on |
|---------|---------|------------|
| `Affiant.Abstractions` | All primitive types (`Affidavit`, `ProvenanceTag`, `ToolEnvelope`, `DocketEntry`) and every framework interface (`IWriteExecutor`, `IFieldMapper<T>`, `IDocketStore`, …), including the neutral tool-interception contract (`IToolInvocationFilter`). Reference this alone to implement a contract. | *(nothing)* |
| `Affiant.Core` | Concrete services: `ContextFabric`, `ReviewGate`, task-inference merge, the deterministic short-circuit, the backend-neutral tool-invocation pipeline, DI wiring. | Abstractions |
| `Affiant.SemanticKernel` | Semantic Kernel interception bridge — translates SK's function-invocation filter pipeline into the neutral pipeline; connector capabilities, structured-output inference. | Core |
| `Affiant.AgentFramework` | Microsoft Agent Framework (MAF) interception bridge — translates MAF's function-calling middleware into the same neutral pipeline; tool catalog reflection, hosted-tool coverage audit. See [`docs/adapters/microsoft-agent-framework.md`](docs/adapters/microsoft-agent-framework.md). | Core |
| `Affiant.Extensions.AI` | Microsoft.Extensions.AI (M.E.AI) interception bridge — the lower-level abstraction both SK and MAF sit on top of. Wraps each `AIFunction` in a `DelegatingAIFunction` that runs the neutral pipeline at M.E.AI's own function-calling seam; tool catalog reflection, hosted-tool coverage audit, double-wrap guard. Provider-neutral: references `Microsoft.Extensions.AI` only, never a concrete provider client. One adapter per tool catalog — never wire this alongside `Affiant.AgentFramework` over the same tools. | Core |
| `Affiant.Docket` | The review queue's backend-neutral half — `InMemoryDocketStore` plus the background expiry sweep that re-broadcasts still-pending Evidence Cards. Pulls no database driver. | Abstractions, Core |
| `Affiant.EntityFramework` | EF Core persistence for sessions and dockets — row-per-message schema, migrations, and the SQLite/Postgres `IChatSessionStore` **and** `IDocketStore` implementations. | Abstractions, Core |
| `Affiant.Policies` | Fluent approval policy graph — Standing Orders (auto-approval), Referrals (escalation), reviewer confirmation, and the seam for a host-written risk score. | Core |
| `Affiant.Transport.SignalR` | SignalR streaming transport and Evidence Card round-trip hub. | Core |
| `Affiant.Testing.ComplianceHarness` | Ship provenance testing as a product: `ComplianceHarness.Verify(...)` proves every write strategy has a paired fixture asserting *substantive* provenance, against either interception backend. For your CI, not just ours. | Core |

---

## Why trust this

The strongest argument for field-level provenance is a mistake this framework's own history
records.

On 30 April 2026, during the extraction of the framework out of its first host application, a
refactoring commit (`b72c1fa`) began shipping **empty Affidavits** — every proposed write
carried fields tagged `ProvenanceSource.Empty` and no real values. The entire test suite —
330 of 330 tests — stayed green, because those tests asserted the *shape* of an Affidavit,
not the *substance* of its provenance. The regression surfaced only when a real user typed a
real message and the review card came up blank.

It was found, fixed, and the whole extraction was then audited field-by-field to 100%
closure. Out of that came the lesson the framework is now built to enforce: **a test suite
can be 100% green and 0% truthful if it asserts structure, not meaning.** `Affiant.Testing.ComplianceHarness`
exists so that provenance-substance is a CI gate — an unpaired or shape-only write strategy
fails the build, in your project as well as ours. This is why the seventh rule ("every field
carries provenance, no exceptions") is a hard invariant and not a guideline.

---

## Positioning

Affiant complements Microsoft's agent stack; it does not compete with it.

- **Entra Agent ID governs *agent* identity — who acted.** Affiant governs *data* identity —
  where each written value came from. They answer different questions and compose.
- **Microsoft Agent Framework (MAF) approval gates the *call*.** Affiant records provenance
  and evidence for each *field*. Approval-gating and field-level provenance are orthogonal;
  you can run both.
- **Semantic Kernel, Microsoft Agent Framework, and Microsoft.Extensions.AI, all three
  first-class** (MAF joined 2026-07-05; M.E.AI joined 2026-08-20). The interception thesis —
  provenance tagging, task inference, review gating — is defined once, backend-neutrally, in
  `Affiant.Core`; `Affiant.SemanticKernel`, `Affiant.AgentFramework`, and `Affiant.Extensions.AI`
  are thin bridges over SK's function-invocation filter pipeline, MAF's function-calling middleware
  (`FunctionInvocationContext`), and M.E.AI's own `FunctionInvokingChatClient` seam respectively.
  Earlier planning assumed a MAF port could stay confined to a new
  `Affiant.SemanticKernel`-adjacent package with no other changes; that premise turned out to be
  false — `Affiant.Core` and `Affiant.Abstractions` still carried direct Semantic Kernel
  dependencies, which a second backend could not share. Extracting a genuinely neutral pipeline out
  of that coupling was the actual work, and it is what let the third backend (M.E.AI) land as
  "just" a new adapter package with no changes to `Affiant.Core`; see
  `docs/proposals/affiant-maf-adapter.md` for the MAF design,
  `docs/adapters/microsoft-agent-framework.md` for the MAF host-facing guide, and
  `docs/overnight-mission-2026-08-20/meai-adapter-design.md` (`affiant-chancery` repo) for the M.E.AI
  design brief.
- **Locally-invoked MCP tool writes flow through the same seam.** MCP tools that *your
  client* invokes funnel through the same function-invocation context, so an adapter
  intercepts those writes for free — field-level provenance for local MCP tool writes.
- **Honest boundary: hosted / server-side tools are out of interception reach.** Tools
  executed by the provider runtime (hosted MCP, code interpreter, web search, and other
  server-side tools) never enter the client's function-invocation pipeline — no framework
  middleware fires for them, so Affiant cannot swear to writes it never sees. Keep
  hosted-tool writes read-only or route them through a reviewed path if you need them
  sworn. (Verified against Microsoft primary documentation, 2026-07-04.)

---

## Versioning & compatibility

Affiant follows [SemVer 2.0](https://semver.org/spec/v2.0.0.html). The ten packages version
**in lockstep** — one version number for all of them, since `2026-07-05` — mechanics and the
per-release detail are in the [CHANGELOG](CHANGELOG.md)'s header; not repeated here.

**What a prerelease tag promises, stated explicitly because SemVer itself doesn't define it:**

- **Between any two prerelease tags (`-alpha.N` → `-alpha.N+1`, or `-beta.N` → `-beta.N+1`), the
  public API carries no stability promise.** A prerelease consumer has opted in
  (`dotnet add package --prerelease` or an explicit version) and should expect that a type
  shape, a DI extension signature, or a package boundary can change in the very next prerelease
  tag with no deprecation window — pin the exact version, don't float on `--prerelease`, and
  read the CHANGELOG before bumping.
- **A breaking prerelease change is still always documented** — every Area-8 breaking change in
  the current `[Unreleased]` CHANGELOG section is labeled `!` in its commit and spelled out in
  prose, including the concrete migration. "No stability promise" describes what's allowed to
  break, not permission to leave a break undocumented.
- **After `1.0.0` GA, standard SemVer applies:** a breaking change requires a major-version bump
  across all ten packages (lockstep means one package's break is every package's major bump); a
  deprecation gets an `[Obsolete]` attribute with a removal-target version stated in its message
  before removal, not simultaneous with it (see `IDeterministicFieldSource`'s current
  `[Obsolete]`-then-scheduled-removal treatment in the CHANGELOG for the pattern in practice).
- **What actually enforces this, mechanically, as of `1.0.0-beta.1`:** every packable project
  carries `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` baselines and references
  `Microsoft.CodeAnalysis.PublicApiAnalyzers` — an undeclared or silently-deleted public member
  fails the build (`RS0016`/`RS0017`). `EnablePackageValidation` is on for all ten packages,
  diffing the packed public surface against `PackageValidationBaselineVersion` once one is set
  (deliberately unset today — there is no published version yet to diff against). Together these
  catch *accidental* drift; they do not and cannot enforce the *policy* above, which is about
  what a maintainer is allowed to change on purpose between tags — that's a human commitment,
  recorded here and in the CHANGELOG, not a build gate.

## Beta status

This is `1.0.0-beta.1`, published on nuget.org on 2026-08-23 (UTC). Earlier `1.0.0-alpha.*`
versions were internal and were never published.

The API has been exercised by two independent first-party host applications, but it has not
yet reached 1.0 GA. Adopt on this basis:

- **Trust the invariant.** Every Affidavit field carries provenance, no exceptions. That
  contract is stable and is enforced by the ComplianceHarness.
- **Expect API evolution between prerelease tags** — see
  ["Versioning & compatibility"](#versioning--compatibility) above for exactly what that does
  and doesn't promise.
- **All ten packages ship together.** `Affiant.AgentFramework` and `Affiant.Extensions.AI` are in
  the co-versioned publish set like every other package — install with `--prerelease`.
- **Where this is going.** By status rather than by date: [ROADMAP.md](ROADMAP.md).

---

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

Apache-2.0 is a deliberate choice for a compliance-adjacent framework: it carries an explicit
patent grant, which matters to the enterprise legal teams who scrutinise anything sitting in
the write path to their systems of record, and it deters patent-based threats against adopters.
