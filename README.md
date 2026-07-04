# Affiant

**Sworn provenance for every AI write.**

An *affiant* is one who swears to the truth of a statement. Affiant is a deterministic
evidence layer for .NET agents: every field an AI proposes to write to your database
carries a sworn record of *where that value came from* — user-stated, looked up from an
external system, computed by your own logic, or merely inferred by the model — and nothing
reaches the database until a human has seen that evidence and approved it.

Nothing commits without evidence. Nothing writes without approval.

> **Beta.** The first public release will be `1.0.0-beta.1` (current internal prerelease:
> `1.0.0-alpha.1`, never published). The public API has been validated by two first-party
> host applications but has not yet reached 1.0 GA. Read [Beta status](#beta-status) before
> adopting — trust the *invariant*, expect the *API* to evolve.

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
- Every proposed mutation is a durable, sworn **`Affidavit`** with an **`AggregateConfidence`**,
  reviewed on an **Evidence Card** and persisted for audit whether it is approved or rejected.

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

Affiant plugs into a Semantic Kernel host through dependency injection. Three pieces wire a
write tool with full provenance.

**1. Register the framework** (`Affiant.Core` + the Semantic Kernel adapter):

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantSemanticKernel();
```

**2. Declare an inference strategy** — the field schema for one writable entity. This is the
domain-specific contract the framework uses to request structured output from the model and
to merge inferred values:

```csharp
using Affiant.Abstractions.Interfaces;

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
            new("StartDate", startDate.ToString("yyyy-MM-dd"), null,
                ProvenanceChain.From(ProvenanceTag.FromUser("StartDate"))),
            new("EndDate", endDate.ToString("yyyy-MM-dd"), null,
                ProvenanceChain.From(ProvenanceTag.FromUser("EndDate"))),
            new("LeaveType", leaveType, null,
                ProvenanceChain.From(ProvenanceTag.FromUser("LeaveType"))),
            new("Reason", reason, null,
                ProvenanceChain.From(ProvenanceTag.FromUser("Reason"))),
        };

        var affidavit = new Affidavit(
            OperationType: "create",
            EntityType: "LeaveRequest",
            EntityId: null,
            Fields: fields,
            AggregateConfidence: 1.0f,
            Warnings: [],
            RequiresConfirmation: true);

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

From here you add an `IFieldMapper<T>` (Affidavit ↔ your domain model) and an `IWriteExecutor`
(the one place `SaveChanges` runs), plus a persistence backend (`AddAffiantDocket` /
`AddAffiantEntityFramework`), a policy graph (`AddAffiantPolicies`), and a transport
(`AddAffiantSignalR`). Read tools follow the same shape and return a `ReadResult`. The full
walkthrough — read tools, context extraction, field mapping, error handling, and testing — is
in [`docs/tool-authoring-guide.md`](docs/tool-authoring-guide.md).

Install:

```bash
dotnet add package Affiant.Core --prerelease
dotnet add package Affiant.SemanticKernel --prerelease
```

---

## The packages

Eight co-versioned packages target `net10.0`. The dependency graph is a strict DAG rooted at
`Affiant.Abstractions`, mirroring the `Microsoft.Extensions.*.Abstractions` / `Microsoft.Extensions.*`
convention — depend only on what you need.

| Package | Purpose | Depends on |
|---------|---------|------------|
| `Affiant.Abstractions` | All primitive types (`Affidavit`, `ProvenanceTag`, `ToolEnvelope`, `DocketEntry`) and every framework interface (`IWriteExecutor`, `IFieldMapper<T>`, `IDocketStore`, …). Reference this alone to implement a contract. | *(nothing)* |
| `Affiant.Core` | Concrete services: `ContextFabric`, `ReviewGate`, task-inference merge, the deterministic short-circuit and tool filters, DI wiring. | Abstractions |
| `Affiant.SemanticKernel` | Semantic Kernel adapter — the interception seam (`IAutoFunctionInvocationFilter` pipeline), connector capabilities, structured-output inference. | Core |
| `Affiant.Docket` | The durable review queue — `IDocketStore` with in-memory, SQLite, and Postgres backing stores. | Abstractions, Core, EntityFramework |
| `Affiant.EntityFramework` | EF Core persistence for sessions and dockets — row-per-message schema, migrations. | Abstractions, Core |
| `Affiant.Policies` | Fluent approval policy graph — Standing Orders (auto-approval), Referrals (escalation), reviewer confirmation, risk scoring. | Core |
| `Affiant.Transport.SignalR` | SignalR streaming transport and Evidence Card round-trip hub. | Core |
| `Affiant.Testing.ComplianceHarness` | Ship provenance testing as a product: `ComplianceHarness.Verify(...)` proves every write strategy has a paired fixture asserting *substantive* provenance. For your CI, not just ours. | Core |

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
- **Semantic Kernel today; MAF on the roadmap.** The interception thesis rests on SK's
  function-invocation filter pipeline. MAF exposes a near 1:1 successor (`FunctionInvocationContext`
  with argument access and a terminate flag); a MAF adapter is planned, with the port confined
  to `Affiant.SemanticKernel`.
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

## Beta status

This is `1.0.0-beta.1`. Earlier `1.0.0-alpha.*` versions were internal and were never
published.

The API has been exercised by two independent first-party host applications, but it has not
yet reached 1.0 GA and **will change before it does**. Adopt on this basis:

- **Trust the invariant.** Every Affidavit field carries provenance, no exceptions. That
  contract is stable and is enforced by the ComplianceHarness.
- **Expect API evolution.** Type shapes, DI extension signatures, and package boundaries may
  change between beta and 1.0.0. Pin the version; read the [CHANGELOG](CHANGELOG.md).

---

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

Apache-2.0 is a deliberate choice for a compliance-adjacent framework: it carries an explicit
patent grant, which matters to the enterprise legal teams who scrutinise anything sitting in
the write path to their systems of record, and it deters patent-based threats against adopters.
