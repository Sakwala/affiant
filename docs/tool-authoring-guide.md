---
title: Tool Authoring Guide — Affiant Framework
version: 1.3-alpha
date: 2026-05-02
status: v1.3 — Section 2's DbContext example inverted to lead with per-invocation (scope-factory)
  resolution, direct Scoped constructor injection now called out as the anti-pattern (affiant#21,
  area-3 P2, 2026-08-03); v1.2 added §10 (naming LLM-visible tools, SK vs MAF; Area 2 P2,
  2026-08-02); v1.1 incorporated Story 13.2 unfamiliar-developer feedback
scope: Framework developers writing plugins for Affiant
audience: Developers unfamiliar with Affiant; estimated 30-minute read for understanding all six patterns
related:
  - docs/affiant-framework-specification.md (full spec, sections 1–6 provide context)
  - docs/affiant-framework-specification.md §7 (this guide extracts §7 standalone)
  - apps/HRPortal/src/HRPortal.Api/Plugins/ (read and write plugin examples; in the private Sakwala/affiant-host-apps repo)
  - apps/HRPortal/src/HRPortal.Api/Filters/ (context extractor examples; in the private Sakwala/affiant-host-apps repo)
  - apps/HRPortal/src/HRPortal.Api/FieldMappers/ (field mapper examples; in the private Sakwala/affiant-host-apps repo)
  - apps/HRPortal/src/HRPortal.Api/Agent/Services/ (write executor examples; in the private Sakwala/affiant-host-apps repo)
note: >
  Worked examples cite host code (HR Portal, Meridian) that lives in the private Sakwala/affiant-host-apps repo,
  not in this public framework repo; they are reproduced to illustrate framework usage patterns.
  Host folder conventions (Plugins vs Agent/Plugins, Filters vs Agent/Extractors, FieldMappers vs Agent/FieldMappers)
  are host decisions — Affiant enforces only DI registration, not folder structure.
---

# Tool Authoring Guide — Affiant Framework

## Contents

1. [Introduction and Philosophy](#1-introduction-and-philosophy)
2. [Read Tool Pattern](#2-read-tool-pattern)
3. [Write Tool Pattern](#3-write-tool-pattern)
4. [Context Extraction](#4-context-extraction)
5. [Field Mapping and Write Execution](#5-field-mapping-and-write-execution)
   - 5.1 [IFieldMapper\<T\>](#51-ifield-mapperlttgt)
   - 5.2 [IWriteExecutor](#52-iwriteexecutor)
   - 5.3 [Adding a new entity type to an existing IWriteExecutor](#53-adding-a-new-entity-type-to-an-existing-iwriteexecutor)
6. [Error Handling](#6-error-handling)
7. [Testing](#7-testing)
8. [Appendix: Quick Reference](#8-appendix-quick-reference)
9. [Common Mistakes and Debugging](#9-common-mistakes-and-debugging)
10. [Naming LLM-Visible Tools: SK vs MAF](#10-naming-llm-visible-tools-sk-vs-maf)

---

## 1. Introduction and Philosophy

This guide teaches you to write plugins for the Affiant framework. Plugins are the seams where your domain expertise meets the framework's provenance tracking — they are the only place where domain-specific logic touches the agent pipeline.

**Three principles govern every plugin you write:**

1. **All tools return `ToolEnvelope` — never throw.** Exceptions that escape a plugin become raw strings in the LLM's reasoning context, causing hallucinated recovery attempts. Catch everything; return a structured `ToolError`.

2. **Read tools never write; write tools never execute.** Read tools return a snapshot of current state. Write tools return a *proposal* — a `WriteProposal` containing an `Affidavit` — which the framework queues for review. The actual database mutation happens only after the user approves, via `IWriteExecutor`.

3. **Every proposed field value carries provenance.** Not just the value, but *why you believe that value*: did the user state it directly? Was it computed from business logic? Was it looked up from a database? The `ProvenanceChain` on every `AffidavitField` answers this question for the reviewer.

**What you will learn in this guide:**

- Section 2: How to write a read tool that serves both the LLM (markdown) and the UI (structured entities)
- Section 3: How to write a write tool that proposes a mutation with full per-field provenance
- Section 4: How to wire a `ContextExtractor` so the LLM can reference read results across turns
- Section 5: How to implement `IFieldMapper<T>` and `IWriteExecutor` to bridge the framework and your domain model
- Section 6: Error handling patterns — what to catch, what to return, when to mark `Retryable`
- Section 7: Integration test patterns that validate `ToolEnvelope` structure, provenance correctness, and Rule 3 compliance

---

## 2. Read Tool Pattern

**Problem:** You need the agent to fetch and present existing data. The LLM needs a readable summary; the UI needs structured entities it can reference later.

**Contract:** Read tools return a `ReadResult` — a markdown string for the LLM plus an `EntityRef[]` for the context fabric. No side effects; no writes.

**Key concepts:**

- Method signature: `[KernelFunction]` + `Task<string>` return type + `[Description]` on each parameter
- Nullable parameters signal "filter by this only if provided" — the LLM will omit them when not relevant
- Markdown format: table rows work well; include entity identifiers in a way the LLM can reference them
- `EntityRef` carries a flat `Dictionary<string, object>` of fields — this is the contract that `IFieldMapper<T>` reads
- Always serialize the return value with `.ToJsonString()` from `ToolEnvelopeExtensions`

**Worked example — entity search read tool:**

> **Corrected 2026-08-03 (affiant#21).** This section previously injected `HRPortalDbContext`
> directly into the plugin's constructor as the *primary* example, with per-invocation resolution
> demoted to an "if your plugin is registered as a singleton" alternative below it. That framing
> was backwards and the guide's own fault, not a host mistake: `kernelBuilder.Plugins.AddFromType<T>()`
> — the registration this very guide teaches — makes **every** SK plugin a root-cached singleton
> (SK does not offer a per-invocation plugin lifetime), so constructor-injecting a `Scoped` service
> such as a `DbContext` is **always** a captive dependency for any plugin registered this way, not
> a conditional risk. Under `ServiceProviderOptions.ValidateScopes` (on by default in ASP.NET Core
> Development) this throws at first invocation; with validation off, it silently pins one request's
> `DbContext` for the process lifetime and shares it, unsynchronized, across every concurrent
> conversation — a live concurrency hazard, not a hypothetical one. The HR reference host copied
> this guide's original ordering faithfully and shipped seven plugin instances of the captive
> pattern (see `affiant#19`'s second leg, `affiant#21`). **The rule, corrected:** per-invocation
> resolution (`IServiceScopeFactory`, shown below) is the default for any plugin dependency with a
> `Scoped` lifetime — not an opt-in alternative for singletons specifically, since every SK plugin
> *is* effectively a singleton under this framework's own recommended registration path. Direct
> constructor injection of a `Scoped` service into a plugin is the anti-pattern; call it out in
> review the same way you would a captive-dependency bug anywhere else in the codebase.

```csharp
// From (the private Sakwala/affiant-host-apps repo): apps/HRPortal/src/HRPortal.Api/Agent/Plugins/SearchEmployeesPlugin.cs
// Imports: using System.ComponentModel; using System.Text; using Affiant.Abstractions.Models;
//          using Microsoft.Extensions.DependencyInjection; using Microsoft.SemanticKernel;
//          using Microsoft.EntityFrameworkCore;
// Dependencies: IServiceScopeFactory (injected — resolves HRPortalDbContext per invocation),
//               HRPortal.Api.Models.Employee

public class SearchEmployeesPlugin(IServiceScopeFactory scopeFactory)
{
    [KernelFunction, Description("Search for employees by name (partial match, case-insensitive), " +
        "email (exact), or department (exact). " +
        "Returns a markdown table and entity references extracted into conversation context. " +
        "Omit all parameters to list all employees (max 100).")]
    public async Task<string> SearchEmployees(
        [Description("Partial name to search for (case-insensitive)")] string? nameQuery = null,
        [Description("Exact email address to match (case-insensitive)")] string? email = null,
        [Description("Exact department name to match (case-insensitive)")] string? department = null,
        CancellationToken cancellationToken = default)
    {
        const string toolName = "SearchEmployees";

        try
        {
            // DbContext is Scoped; every SK plugin registered via AddFromType<T>() is a
            // root-cached singleton, so a fresh scope per invocation is not optional — see the
            // correction note above. Never inject HRPortalDbContext directly into this
            // constructor.
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<HRPortalDbContext>();

            // Build a composable query — only add filters for parameters that were provided.
            var query = dbContext.Employees.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(nameQuery))
                query = query.Where(e => e.Name.ToLower().Contains(nameQuery.Trim().ToLower()));

            if (!string.IsNullOrWhiteSpace(email))
                query = query.Where(e => e.Email.ToLower() == email.Trim().ToLower());

            if (!string.IsNullOrWhiteSpace(department))
                query = query.Where(e => e.Department.ToLower() == department.Trim().ToLower());

            var employees = await query.OrderBy(e => e.Name).Take(100).ToListAsync(cancellationToken);

            // Build markdown for the LLM — a table the model can read and quote from.
            var sb = new StringBuilder();
            sb.AppendLine($"## Search Results: {employees.Count} employee(s) found");
            sb.AppendLine();

            if (employees.Count == 0)
            {
                sb.AppendLine("*No employees matched the search criteria.*");
            }
            else
            {
                sb.AppendLine("| Name | Email | Department | Manager ID |");
                sb.AppendLine("|------|-------|------------|-----------|");
                foreach (var emp in employees)
                {
                    var mgr = emp.ManagerId.HasValue ? emp.ManagerId.Value.ToString() : "(none)";
                    sb.AppendLine($"| {emp.Name} | {emp.Email} | {emp.Department} | {mgr} |");
                }
            }

            // Build EntityRef[] for the context fabric — field keys must match IFieldMapper<T>.
            var entities = employees.Select(emp => new EntityRef(
                EntityType: "Employee",
                EntityId: emp.EmployeeId.ToString(),
                DisplayName: emp.Name,
                Fields: new Dictionary<string, object>
                {
                    ["Name"]       = emp.Name,
                    ["Email"]      = emp.Email,
                    ["Department"] = emp.Department,
                    ["ManagerId"]  = emp.ManagerId?.ToString() ?? string.Empty,
                })).ToArray();

            return new ReadResult(toolName, DateTimeOffset.UtcNow,
                $"Found {employees.Count} employee(s)", sb.ToString(), entities).ToJsonString();
        }
        catch (Exception ex) when (ex is TimeoutException or DbUpdateException)
        {
            return new ToolError(toolName, DateTimeOffset.UtcNow,
                "DB_TIMEOUT", "Database is temporarily unavailable. Please try again.",
                Retryable: true).ToJsonString();
        }
    }
}
```

**Annotations:**

- The composable query pattern (`AsQueryable()` + successive `.Where()` calls) is standard across all read tools with optional filter parameters
- `AsNoTracking()` is essential for read-only queries — skips EF Core's change-tracking overhead
- Return empty `EntityRef[]` (not an error) when zero results are found — let the LLM reason about the empty result
- `.ToJsonString()` serializes with the `$type` discriminator that the UI layer uses for polymorphic deserialization

**The scope-factory pattern is the rule, not an alternative (affiant#21).** The worked example
above already uses it. A second host example, for completeness — this is the same pattern, not a
different one to choose between:

```csharp
// From (the private Sakwala/affiant-host-apps repo): apps/Meridian/src/Meridian.Api/Agent/Plugins/InventoryPlugin.cs (pattern excerpt)
public class InventoryPlugin(IServiceScopeFactory scopeFactory)
{
    public async Task<string> SearchParts(...)
    {
        // DbContext is scoped; every SK plugin is effectively a singleton (AddFromType<T>()
        // has no per-invocation lifetime) — a fresh scope per invocation is mandatory, not
        // situational. See the correction note earlier in this section (affiant#21).
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // ... rest of query
    }
}
```

**Anti-pattern (do not do this):** `public class SearchEmployeesPlugin(HRPortalDbContext dbContext)`
— constructor-injecting any `Scoped` dependency directly into a plugin class. It compiles, and it
may even pass a quick manual test, which is exactly why it is dangerous: the failure is either a
`ValidateScopes` startup/first-call crash (if host validation is on) or a silent shared-`DbContext`
concurrency bug across every concurrent conversation (if it is off). There is no registration
pattern taught in this guide under which direct `Scoped` constructor injection into a plugin is
safe.

**When to use the scope-factory pattern:** Every `[KernelFunction]` — read or write — that depends
on a `Scoped` service (a `DbContext`, or any per-request dependency your host registers `Scoped`).

---

## 3. Write Tool Pattern

**Problem:** The agent needs to propose a change to domain data. You want full auditability — who asked for what, where each value came from, and how confident you are — before any record is written.

**Contract:** Write tools return a `WriteProposal` wrapping an `Affidavit` with per-field provenance. No database mutations happen in this method.

**Key concepts:**

- Parameters express *user intent* — nullable parameters mean "only include this field if the user provided it"
- `AffidavitField` records the proposed value, the previous value (null for creates), and a `ProvenanceChain`
- `ProvenanceChain.From(tag)` creates a single-node chain; use `.Append(tag)` to accumulate history
- `ProvenanceTag.FromUser(fieldName)` tags a value the user stated directly; `new ProvenanceTag(Computed, ...)` for derived values
- `Affidavit.RequiresConfirmation: true` tells the `ReviewGate` to show an Evidence Card before committing

**Worked example — multi-field write proposal with mixed provenance:**

```csharp
// From (the private Sakwala/affiant-host-apps repo): apps/HRPortal/src/HRPortal.Api/Agent/Plugins/RequestLeavePlugin.cs
// Imports: using System.ComponentModel; using System.Diagnostics; using Affiant.Abstractions.Models;
//          using Affiant.Core.Observability; using Microsoft.SemanticKernel;
//          using Microsoft.EntityFrameworkCore;
// Dependencies: HRPortalDbContext (injected), HRPortal.Api.Models.*

public class RequestLeavePlugin(HRPortalDbContext dbContext, ILogger<RequestLeavePlugin> logger)
{
    private static readonly string[] ValidLeaveTypes = ["Annual", "Sick", "Personal"];

    [KernelFunction("request_leave")]
    [Description("Propose a leave request for the current employee. Returns a WriteProposal for " +
        "user confirmation before any record is created. Never writes directly.")]
    public async Task<string> RequestLeaveAsync(
        [Description("Start date of the leave period (yyyy-MM-dd).")] DateTime startDate,
        [Description("End date of the leave period (yyyy-MM-dd), inclusive.")] DateTime endDate,
        [Description("Type of leave: Annual, Sick, or Personal.")] string leaveType,
        [Description("Reason for the leave request.")] string reason,
        CancellationToken cancellationToken = default)
    {
        const string toolName = "RequestLeave";

        try
        {
            // Validate before any DB access — non-retryable errors returned early.
            if (startDate.Date >= endDate.Date)
                return new ToolError(toolName, DateTimeOffset.UtcNow, "INVALID_DATES",
                    "Start date must be before end date.", false).ToJsonString();

            if (startDate.Date < DateTime.UtcNow.Date)
                return new ToolError(toolName, DateTimeOffset.UtcNow, "PAST_DATE",
                    "Cannot request leave for dates in the past.", false).ToJsonString();

            var normalizedType = ValidLeaveTypes.FirstOrDefault(
                t => t.Equals(leaveType, StringComparison.OrdinalIgnoreCase));
            if (normalizedType is null)
                return new ToolError(toolName, DateTimeOffset.UtcNow, "INVALID_LEAVE_TYPE",
                    $"Leave type must be Annual, Sick, or Personal. Got: {leaveType}", false).ToJsonString();

            var employeeId = 1; // Auth context placeholder — resolved via ContextFabric in a later story

            var employee = await dbContext.Employees
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.EmployeeId == employeeId, cancellationToken);

            if (employee is null)
                return new ToolError(toolName, DateTimeOffset.UtcNow, "EMPLOYEE_NOT_FOUND",
                    $"Employee {employeeId} not found.", false).ToJsonString();

            // Compute leave balance — this produces a Computed provenance field below.
            var leaveTypeEnum = Enum.Parse<LeaveType>(normalizedType);
            var year = startDate.Year;
            var allocation = await dbContext.LeaveAllocations.AsNoTracking()
                .FirstOrDefaultAsync(la => la.EmployeeId == employeeId
                    && la.LeaveType == leaveTypeEnum && la.Year == year, cancellationToken);

            var totalAllocation = allocation?.AnnualAllocation ?? 0;
            var usedDays = await dbContext.LeaveRequests.AsNoTracking()
                .Where(lr => lr.EmployeeId == employeeId && lr.LeaveType == leaveTypeEnum
                    && lr.Status == LeaveRequestStatus.Approved
                    && lr.StartDate.Year == year)
                .SumAsync(lr => lr.EndDate.DayNumber - lr.StartDate.DayNumber + 1, cancellationToken);

            var currentBalance  = totalAllocation - usedDays;
            var requestedDays   = CalculateWorkingDays(startDate, endDate);
            var remainingAfter  = currentBalance - requestedDays;

            // Build one AffidavitField per proposed field value.
            // UserStated: value came directly from the tool's parameters.
            // Computed: derived by deterministic business logic.
            // One AffidavitField per proposed value.
            // UserStated = came from the tool's parameters (the user said it).
            // Computed = derived by deterministic business logic (balance math).
            var fields = new AffidavitField[]
            {
                new("StartDate",  startDate.ToString("yyyy-MM-dd"), null,
                    ProvenanceChain.From(ProvenanceTag.FromUser("StartDate"))),
                new("EndDate",    endDate.ToString("yyyy-MM-dd"), null,
                    ProvenanceChain.From(ProvenanceTag.FromUser("EndDate"))),
                new("LeaveType",  normalizedType, null,
                    ProvenanceChain.From(ProvenanceTag.FromUser("LeaveType"))),
                new("Reason",     reason, null,
                    ProvenanceChain.From(ProvenanceTag.FromUser("Reason"))),
                new("RemainingDaysAfter", remainingAfter.ToString(), null,
                    ProvenanceChain.From(new ProvenanceTag(
                        ProvenanceSource.Computed, 1.0f,
                        $"Computed: currentBalance({currentBalance}) - requestedDays({requestedDays})",
                        null))),
            };

            string[] warnings = remainingAfter < 0
                ? [$"Insufficient balance: {remainingAfter} days remaining after request."]
                : [];

            var affidavit = new Affidavit(
                OperationType: "create",
                EntityType:    "LeaveRequest",
                EntityId:      null,         // null = create operation; non-null = update
                Fields:        fields,
                AggregateConfidence: 1.0f,
                Warnings: warnings,
                RequiresConfirmation: true); // ReviewGate will show an Evidence Card

            // This is a WriteProposal — no database mutation happens here.
            // IWriteExecutor.ExecuteAsync is called only after ReviewGate approval.
            return new WriteProposal(toolName, DateTimeOffset.UtcNow, affidavit).ToJsonString();
        }
        catch (Exception ex) when (ex is TimeoutException or DbUpdateException)
        {
            return new ToolError(toolName, DateTimeOffset.UtcNow, "DB_TIMEOUT",
                "Database is temporarily unavailable. Please try again.", Retryable: true).ToJsonString();
        }
    }

    private static int CalculateWorkingDays(DateTime start, DateTime end)
    {
        var count = 0;
        for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
            if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
                count++;
        return count;
    }
}
```

**`ProvenanceSource` quick reference:**

| Source | Meaning | Typical confidence |
|--------|---------|--------------------|
| `UserStated` | User explicitly stated this value | 1.0 |
| `External` | Fetched from an external system | 0.8–0.95 |
| `Computed` | Derived by deterministic business logic | 0.9–1.0 |
| `Conversation` | Mentioned in chat but not stated as a value | 0.7 |
| `Inferred` | LLM inferred from conversational signals | 0.5–0.7 |
| `Default` | System default or fallback | 0.2–0.4 |
| `Empty` | Provenance unknown — tag explicitly, never omit | 0.0 |

**When to use:** Every `[KernelFunction]` that proposes a database mutation (create, update, delete).

**Common variations:**

- *Updates vs. creates*: Set `EntityId` to the existing record's ID for updates; leave it `null` for creates
- *Single-field updates*: An `Affidavit` can have one field; the review UI shows exactly what's changing
- *Multiple provenance sources*: Mix `UserStated` (from parameters) and `Computed` (from business logic) in the same affidavit — each field carries its own chain

---

## 4. Context Extraction

**Problem:** After a read tool returns results, the LLM may reference those entities in subsequent turns ("Update that employee's email"). Without a mechanism to store entity state, each new turn lacks the context to resolve those references.

**Solution:** A `ContextExtractor` is a post-invocation filter that fires after a read tool, deserializes the `ReadResult`, and upserts each entity into the `ContextFabric` — a conversation-scoped store the framework queries when building system context.

**Contract:** Subclass `Affiant.Core.Filters.ContextExtractor`. Override `MatchesTool` to identify your plugin, and `ExtractAsync` to call `EmitEntity` for each entity in the result.

**Worked example — read tool context extractor:**

```csharp
// From (the private Sakwala/affiant-host-apps repo): apps/HRPortal/src/HRPortal.Api/Agent/Extractors/EmployeeSearchExtractor.cs
// Imports: using Affiant.Abstractions.Models; using Affiant.Core.Filters;
//          using Affiant.Core.Services; using Microsoft.Extensions.Logging;
//          using Microsoft.SemanticKernel;
// Dependencies: ContextFabric (injected via ContextExtractor base), ILogger

public class EmployeeSearchExtractor(
    ContextFabric contextFabric,
    ILogger<EmployeeSearchExtractor> logger)
    : ContextExtractor(contextFabric, logger)
{
    // Returns true only for the tool this extractor handles.
    // OrdinalIgnoreCase matches SK's registration regardless of casing.
    protected override bool MatchesTool(string toolName) =>
        toolName.Equals("SearchEmployees", StringComparison.OrdinalIgnoreCase);

    // Called after the tool runs, with the deserialized ReadResult.
    // Call EmitEntity once per entity to upsert it into the ContextFabric.
    protected override Task ExtractAsync(ReadResult result, FunctionInvocationContext context)
    {
        foreach (var entity in result.Entities)
            EmitEntity(entity);
        return Task.CompletedTask;
    }
}
```

**How the base class works:**

The `ContextExtractor` base class wires `IFunctionInvocationFilter`, calling `await next(context)` to let the tool execute first, then deserializing the returned JSON and dispatching to `ExtractAsync` only if `MatchesTool` returns true. You never need to parse JSON or handle null results directly.

**DI registration:**

```csharp
// In Program.cs / Startup.cs
// From (the private Sakwala/affiant-host-apps repo): apps/HRPortal/src/HRPortal.Api/Program.cs
builder.Services.AddScoped<IFunctionInvocationFilter, EmployeeSearchExtractor>();
```

**Host folder convention:** The base class lives in the `Affiant.Core.Filters` namespace, which makes `Filters/` a natural folder name in your host project. Some hosts use `Agent/Extractors/` instead. Both are valid — DI registration determines behavior, not folder structure. When adding a new extractor, use whatever convention your host already has; if starting fresh, `Filters/` matches the framework namespace.

**Testability:** For simple extractors that only call `EmitEntity` (like `ExpenseReportSearchExtractor`), direct unit testing is typically unnecessary — the read plugin integration test that verifies `EntityRef[]` output covers the meaningful behaviour. For extractors that tag individual fields with typed provenance chains, expose a `public ProcessEntity(EntityRef entity)` method so tests can call it directly without wiring the full SK filter pipeline:

```csharp
// Extractor exposes ProcessEntity for direct test access
public void ProcessEntity(EntityRef entity)
{
    EmitEntity(entity); // base class — calls ContextFabric.Upsert()
    // tag individual fields with External/Computed provenance if needed
}

// In the test:
var fabric = new ContextFabric();
var extractor = new MyEntityExtractor(fabric, NullLogger<MyEntityExtractor>.Instance);
extractor.ProcessEntity(entityRef);
Assert.True(fabric.Snapshot().ContainsKey(entityRef.EntityId));
```

**What happens in the background:** `EmitEntity` calls `ContextFabric.Upsert(entityRef)`, which stores the entity in a conversation-scoped `ConversationContext`. On session rehydration, the Docket restores this context so the LLM has access to previously-fetched entities without re-querying.

**When to use:** Implement a `ContextExtractor` for every read tool that returns structured `EntityRef[]` that the LLM might reference in future turns. Simple lookups (e.g., date/time queries) that return no entities do not need one.

**Common questions:**

- *"Do I need one per read tool?"* Only for tools with entity-rich results. A "what's today's date?" tool doesn't produce entities.
- *"Can the extractor modify what the LLM sees?"* No. The filter runs after the LLM receives the result. `ContextExtractor` is purely for internal state tracking.

### 4.1 ContextFabric lifetime contract (read before registering it)

`ContextFabric` / `IContextFabric` is **conversation-scoped**: `AddAffiantCore()` registers it with the DI `Scoped` lifetime, giving each conversation turn its own instance. The framework relies on this — the neutral tool-invocation pipeline resolves the fabric (and every filter) from the caller's turn scope, so one turn's inference, merge, and review-gate stages all read and write the same instance while concurrent turns stay fully isolated.

**Hosts MUST NOT re-register the fabric as a singleton.** A singleton fabric is shared by every concurrent conversation. Because entities and field chains are keyed by bare entity/field names (no conversation namespace), one conversation's values overwrite another's (value bleed), and the `Clear()` method — intended for per-session cleanup — would wipe a concurrent conversation's provenance to `ProvenanceTag.Empty` mid-projection. `AddAffiantCore()` uses `TryAdd`, so a host `AddSingleton<ContextFabric>()` registered *before* it silently wins and reintroduces exactly this bug.

**Consequences for host code:**

- Any service that captures `ContextFabric` / `IContextFabric` by constructor (e.g. a `ContextExtractor` subclass, a custom filter) must itself be registered `Scoped` or `Transient` — never `Singleton`. A singleton capturing the scoped fabric is a captive dependency and fails `ValidateScopes`.
- Resolve the agent/kernel from the per-request (turn) scope so the ambient scope the bridges hand the pipeline carries that turn's fabric. Resolving from the root provider defeats the isolation.

---

## 5. Field Mapping and Write Execution

**Problem:** The framework operates on generic `Affidavit` with `string` field names and `object` values. Your domain model is strongly typed. You need a bridge that converts in both directions and enforces domain constraints.

**Two interfaces:**

- `IFieldMapper<TDomainModel>`: Converts between `Affidavit` ↔ domain model
- `IWriteExecutor`: Takes an approved `Affidavit`, maps it, and persists via `DbContext`

### 5.1 IFieldMapper\<T\>

**Worked example — bidirectional field mapper:**

```csharp
// From (the private Sakwala/affiant-host-apps repo): apps/HRPortal/src/HRPortal.Api/Agent/FieldMappers/LeaveRequestFieldMapper.cs
// Imports: using Affiant.Abstractions.Models; using HRPortal.Api.Models;
// Dependencies: ILeaveRequestFieldMapper (extends IFieldMapper<LeaveRequest>), ILogger

public class LeaveRequestFieldMapper(ILogger<LeaveRequestFieldMapper> logger) : ILeaveRequestFieldMapper
{
    // MapFromAffidavit: Affidavit (framework type) → domain model (used by WriteExecutor)
    public LeaveRequest MapFromAffidavit(Affidavit affidavit)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        // Build a lookup by field name for O(1) access.
        var fieldDict = affidavit.Fields.ToDictionary(f => f.Name);

        // Validate required fields before parsing.
        foreach (var required in new[] { "StartDate", "EndDate", "LeaveType", "Reason" })
        {
            if (!fieldDict.ContainsKey(required))
                throw new InvalidOperationException($"Affidavit missing required field '{required}'");
        }

        // Parse each field explicitly — AffidavitField.Value is object?, so cast and convert.
        if (!DateOnly.TryParse(fieldDict["StartDate"].Value?.ToString(), out var startDate))
            throw new FormatException($"Cannot parse StartDate: {fieldDict["StartDate"].Value}");

        if (!DateOnly.TryParse(fieldDict["EndDate"].Value?.ToString(), out var endDate))
            throw new FormatException($"Cannot parse EndDate: {fieldDict["EndDate"].Value}");

        if (!Enum.TryParse<LeaveType>(fieldDict["LeaveType"].Value?.ToString(), true, out var leaveType))
            throw new FormatException($"Cannot parse LeaveType: {fieldDict["LeaveType"].Value}");

        var reason = fieldDict["Reason"].Value?.ToString() ?? string.Empty;

        // Enforce domain invariant — this is the right layer for domain-level validation.
        if (endDate < startDate)
            throw new ArgumentException("EndDate cannot be before StartDate");

        // Optional field: graceful null coalescing for fields not always present.
        var employeeId = 0;
        if (fieldDict.TryGetValue("EmployeeId", out var empField))
            int.TryParse(empField.Value?.ToString(), out employeeId);

        return new LeaveRequest(
            RequestId: 0,
            EmployeeId: employeeId,
            StartDate: startDate,
            EndDate: endDate,
            LeaveType: leaveType,
            Status: LeaveRequestStatus.PendingManagerApproval,
            Reason: reason);
    }

    // MapToAffidavit: domain model → Affidavit (used by read tools and audit)
    public Affidavit MapToAffidavit(LeaveRequest entity, string operationType)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var fields = new AffidavitField[]
        {
            new("StartDate", entity.StartDate.ToString("yyyy-MM-dd"), null,
                ProvenanceChain.From(ProvenanceTag.FromUser("StartDate"))),
            new("EndDate", entity.EndDate.ToString("yyyy-MM-dd"), null,
                ProvenanceChain.From(ProvenanceTag.FromUser("EndDate"))),
            new("LeaveType", entity.LeaveType.ToString(), null,
                ProvenanceChain.From(ProvenanceTag.FromUser("LeaveType"))),
            new("Reason", entity.Reason, null,
                ProvenanceChain.From(ProvenanceTag.FromUser("Reason"))),
        };

        return new Affidavit(
            OperationType: operationType,
            EntityType:    "LeaveRequest",
            EntityId:      entity.RequestId == 0 ? null : entity.RequestId.ToString(),
            Fields:        fields,
            AggregateConfidence: 1.0f,
            Warnings: [],
            RequiresConfirmation: false);
    }
}
```

**Annotations:**

- Field names in `Affidavit` must exactly match the keys you look up in `MapFromAffidavit`
- Handle null and type mismatch explicitly — raise a domain-meaningful exception rather than letting a `NullReferenceException` propagate
- Domain constraints belong here: date ordering, required fields, enum membership

**Marker interface note:** `ILeaveRequestFieldMapper` is a host-specific marker interface that extends `IFieldMapper<LeaveRequest>`. Marker interfaces are optional — they are useful when a host has multiple mappers and needs to differentiate them in constructor injection. If your executor accepts `IFieldMapper<T>` directly (as `HRWriteExecutor` does for `ExpenseReport`), skip the marker interface and register against the generic type:

```csharp
// No marker interface required — IFieldMapper<T> is sufficient
services.AddScoped<IFieldMapper<ExpenseReport>, ExpenseReportFieldMapper>();
```

Use a host-specific interface only if the same `IWriteExecutor` injects multiple mappers that the compiler cannot distinguish by `IFieldMapper<T>` alone (e.g., two mappers for the same `T`).

### 5.2 IWriteExecutor

**Worked example — write executor with entity-type dispatch:**

```csharp
// From (the private Sakwala/affiant-host-apps repo): apps/HRPortal/src/HRPortal.Api/Agent/Services/HRWriteExecutor.cs
// Imports: using Affiant.Abstractions.Interfaces; using Affiant.Abstractions.Models;
//          using HRPortal.Api.Agent.FieldMappers; using HRPortal.Api.Data;
//          using Microsoft.EntityFrameworkCore;
// Dependencies: HRPortalDbContext, ILeaveRequestFieldMapper, ILogger

public class HRWriteExecutor(
    HRPortalDbContext dbContext,
    ILeaveRequestFieldMapper leaveRequestMapper,
    ILogger<HRWriteExecutor> logger) : IWriteExecutor
{
    public async Task<string?> ExecuteAsync(
        Affidavit affidavit,
        Dictionary<string, object>? amendments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        // Route to the correct domain handler by EntityType.
        // Add a new case here when you support a new writable entity.
        return affidavit.EntityType switch
        {
            "LeaveRequest" => await ExecuteLeaveRequestAsync(affidavit, amendments, ct),
            _              => throw new NotImplementedException(
                                  $"No executor for entity type '{affidavit.EntityType}'")
        };
    }

    private async Task<string?> ExecuteLeaveRequestAsync(
        Affidavit affidavit,
        Dictionary<string, object>? amendments,
        CancellationToken ct)
    {
        // 1. Map approved Affidavit → domain model using the registered IFieldMapper<T>.
        var mapped = leaveRequestMapper.MapFromAffidavit(affidavit);

        // 2. Apply any reviewer amendments (reviewer may have corrected a field during review).
        var employeeId = ResolveEmployeeId(mapped.EmployeeId, amendments);

        // 3. Re-validate business invariants at commit time — the proposal may be stale.
        var employee = await dbContext.Employees
            .FirstOrDefaultAsync(e => e.EmployeeId == employeeId, ct)
            ?? throw new InvalidOperationException($"Employee {employeeId} not found");

        // ... (balance check, overlap detection — see full source) ...

        var newRequest = new LeaveRequest(
            RequestId: 0, EmployeeId: employee.EmployeeId,
            StartDate: mapped.StartDate, EndDate: mapped.EndDate,
            LeaveType: mapped.LeaveType, Status: LeaveRequestStatus.Approved,
            Reason: mapped.Reason);

        // 4. SaveChanges happens ONLY here — never in the plugin, never in the field mapper.
        dbContext.LeaveRequests.Add(newRequest);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("Leave request created: employee={EmployeeId}", employeeId);
        return newRequest.RequestId.ToString(); // Return new entity ID to Docket
    }

    // Amendments allow the reviewer to correct fields during the approval step.
    private static int ResolveEmployeeId(int fromMapper, Dictionary<string, object>? amendments)
    {
        if (amendments?.TryGetValue("EmployeeId", out var val) == true)
        {
            if (val is int i) return i;
            if (int.TryParse(val?.ToString(), out var parsed)) return parsed;
        }
        return fromMapper;
    }
}
```

**DI registration:**

```csharp
// In Program.cs / Startup.cs
services.AddScoped<ILeaveRequestFieldMapper, LeaveRequestFieldMapper>();
services.AddScoped<IWriteExecutor, HRWriteExecutor>();
```

**Write flow diagram:**

```
Plugin returns WriteProposal
    ↓
ReviewGate queues Evidence Card for user review
    ↓
User approves (optionally amends fields)
    ↓
ReviewGate calls IWriteExecutor.ExecuteAsync(approvedAffidavit, amendments, ct)
    ↓
WriteExecutor calls IFieldMapper<T>.MapFromAffidavit(affidavit)
    ↓
WriteExecutor persists domain model via DbContext.SaveChangesAsync()
    ↓
WriteResult entity ID returned; Docket Evidence Card updated
```

### 5.3 Adding a new entity type to an existing IWriteExecutor

If your host already has a write executor handling other entity types, follow this three-step pattern. The key constraint: existing tests that construct the executor directly will break if you simply append a new required parameter to the primary constructor.

**Step 1 — add the new field mapper dependency to the primary constructor:**

```csharp
// From (the private Sakwala/affiant-host-apps repo): apps/HRPortal/src/HRPortal.Api/Agent/Services/HRWriteExecutor.cs
public class HRWriteExecutor(
    HRPortalDbContext dbContext,
    ILeaveRequestFieldMapper leaveRequestMapper,
    IPersonalInfoFieldMapper personalInfoMapper,
    IFieldMapper<ExpenseReport> expenseReportMapper,   // ← new dependency
    ILogger<HRWriteExecutor> logger) : IWriteExecutor
```

**Step 2 — add a backward-compatible overload for tests that construct the executor directly:**

```csharp
// Overload preserves the previous constructor signature.
// Tests that call new HRWriteExecutor(db, leaveMapper, personalMapper, logger) still compile.
public HRWriteExecutor(
    HRPortalDbContext dbContext,
    ILeaveRequestFieldMapper leaveRequestMapper,
    IPersonalInfoFieldMapper personalInfoMapper,
    ILogger<HRWriteExecutor> logger)
    : this(dbContext, leaveRequestMapper, personalInfoMapper,
           new ExpenseReportFieldMapper(NullLogger<ExpenseReportFieldMapper>.Instance),
           logger)
{
}
```

**Step 3 — add the new entity type case to the dispatch switch:**

```csharp
return affidavit.EntityType switch
{
    "LeaveRequest"       => await ExecuteLeaveRequestAsync(affidavit, amendments, ct),
    "PersonalInfoUpdate" => await ExecutePersonalInfoUpdateAsync(affidavit, amendments, ct),
    "ExpenseReport"      => await ExecuteExpenseReportAsync(affidavit, amendments, ct), // ← new
    _ => throw new NotImplementedException(
             $"Write executor does not support entity type '{affidavit.EntityType}'")
};
```

**EF migration:** After adding the new entity's `DbSet<T>` to `DbContext`, generate a migration before running the host:

```bash
dotnet ef migrations add Add<EntityType> --project apps/YourHost/src/YourHost.Api
```

EF's model snapshot accumulates all pending model changes. If the generated migration includes unexpected columns or tables beyond your new entity, review the output carefully — the snapshot may contain drift from earlier uncommitted model edits. Apply only the diff you expect; do not hand-edit EF-generated designer files.

**DI registration for the new mapper:**

```csharp
// Register the new mapper; the executor is already registered via IWriteExecutor
services.AddScoped<IFieldMapper<ExpenseReport>, ExpenseReportFieldMapper>();
```

---

## 6. Error Handling

**Contract:** Plugins must catch all exceptions and return a `ToolError` envelope. Never let an exception propagate to the LLM.

**Why:** An uncaught exception becomes a stack trace string in the LLM's context. The LLM will then hallucinate a recovery strategy. A structured `ToolError` lets the framework decide deterministically whether to retry.

**`ToolError` structure:**

```csharp
// From: src/Affiant.Abstractions/Models/ToolEnvelope.cs
public sealed record ToolError(
    string ToolName,
    DateTimeOffset Timestamp,
    string Code,       // Machine-readable code, e.g. "ENTITY_NOT_FOUND"
    string Message,    // Human-readable explanation
    bool Retryable     // Whether the framework should retry after a backoff
) : ToolEnvelope(ToolName, Timestamp);
```

**Pattern 1 — lookup failure (non-retryable):**

```csharp
var employee = await dbContext.Employees.FindAsync(employeeId, cancellationToken);
if (employee is null)
    return new ToolError(toolName, DateTimeOffset.UtcNow,
        "EMPLOYEE_NOT_FOUND",
        $"No employee found with ID {employeeId}",
        Retryable: false).ToJsonString();
```

**Pattern 2 — parameter validation (non-retryable):**

```csharp
if (startDate.Date >= endDate.Date)
    return new ToolError(toolName, DateTimeOffset.UtcNow,
        "INVALID_DATES",
        "Start date must be before end date.",
        Retryable: false).ToJsonString();
```

**Pattern 3 — transient database failure (retryable):**

```csharp
catch (Exception ex) when (ex is TimeoutException or DbUpdateException)
{
    logger.LogError(ex, "Database error in {ToolName}", toolName);
    return new ToolError(toolName, DateTimeOffset.UtcNow,
        "DB_TIMEOUT",
        "Database is temporarily unavailable. Please try again.",
        Retryable: true).ToJsonString();
}
```

**`Retryable` decision rule:**

| `Retryable: true` | `Retryable: false` |
|-------------------|--------------------|
| Transient failures: DB timeout, connection drop, rate limit | Permanent failures: record not found, validation error, business rule violation |
| Framework retries once after backoff | Framework asks the LLM to handle the error |

> **Known gap, flagged not fixed (area-3, V6/V4).** "Framework retries once after backoff" in the
> right-hand column above describes `ToolErrorFilter`'s behavior for exceptions it *catches and
> classifies itself* (§3.12.9 in the spec). A tool that follows Patterns 1–3 above — directly
> returning a `ToolError` with `Retryable: true` rather than throwing, the pattern this section
> teaches — is never retried by anything: `ToolErrorFilter`'s retry branch only runs from inside
> its own `catch` clause, never on a `ToolError` a tool returns as a normal, non-throwing result.
> This is a real doc/code mismatch for the documented, recommended pattern — not fixed as part of
> the area-3 P2 wave (it changes tool-return semantics, which is a larger surface than the
> pipeline-internal fixes P2 covers) and is called out here so a reader is not misled in the
> meantime.

**Anti-patterns:**

- Throwing exceptions from plugins — never
- Returning generic error messages like `"Error"` — always include `Code` + specific `Message`
- Swallowing exceptions silently — always communicate via `ToolError`
- Returning `ToolError` when the operation *partially* succeeded — be clear about what failed

---

## 7. Testing

**Three test scopes:**

- **Unit tests**: Test field mappers or business logic in isolation — no framework needed
- **Integration tests**: Invoke the plugin end-to-end, verify `ToolEnvelope` structure and provenance *(focus here)*
- **End-to-end tests**: Full agent turn with LLM call — covered by Story 13.2

### Integration test structure

**Worked example — write plugin integration test:**

```csharp
// From (the private Sakwala/affiant-host-apps repo): apps/HRPortal/tests/HRPortal.Api.Tests/Agent/Plugins/RequestLeavePluginTests.cs
// Imports: using System.Text.Json; using Affiant.Abstractions.Models;
//          using HRPortal.Api.Agent.Plugins; using HRPortal.Api.Data;
//          using Microsoft.Data.Sqlite; using Microsoft.EntityFrameworkCore;
//          using Microsoft.Extensions.Logging.Abstractions; using Xunit;

public class RequestLeavePluginTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private HRPortalDbContext _dbContext = null!;
    private RequestLeavePlugin _plugin  = null!;

    private static readonly DateTime FutureStart = DateTime.UtcNow.AddDays(14).Date;
    private static readonly DateTime FutureEnd   = DateTime.UtcNow.AddDays(18).Date;

    public async Task InitializeAsync()
    {
        // SQLite in-memory over a kept-open connection gives a real DB without external state.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbContext = new HRPortalDbContext(new DbContextOptionsBuilder<HRPortalDbContext>()
            .UseSqlite(_connection).Options);
        await _dbContext.Database.EnsureCreatedAsync();
        SeedTestData();
        _plugin = new RequestLeavePlugin(_dbContext, NullLogger<RequestLeavePlugin>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task RequestLeaveAsync_ReturnsWriteProposal()
    {
        var json     = await _plugin.RequestLeaveAsync(FutureStart, FutureEnd, "Annual", "Team offsite");
        var envelope = JsonSerializer.Deserialize<ToolEnvelope>(json, CamelCaseOptions);
        var proposal = Assert.IsType<WriteProposal>(envelope);
        Assert.Equal("RequestLeave", proposal.ToolName);
        Assert.NotNull(proposal.Envelope);
    }

    [Fact]
    public async Task RequestLeaveAsync_FourFieldsUserStated_OneComputed()
    {
        var json = await _plugin.RequestLeaveAsync(FutureStart, FutureEnd, "Annual", "Team offsite");
        using var doc = JsonDocument.Parse(json);
        var fields = doc.RootElement
            .GetProperty("envelope").GetProperty("fields").EnumerateArray()
            .ToDictionary(
                f => f.GetProperty("name").GetString()!,
                f => f.GetProperty("provenance").GetProperty("current").GetProperty("source").GetString()!);

        Assert.Equal("UserStated", fields["StartDate"]);
        Assert.Equal("UserStated", fields["EndDate"]);
        Assert.Equal("UserStated", fields["LeaveType"]);
        Assert.Equal("UserStated", fields["Reason"]);
        Assert.Equal("Computed",   fields["RemainingDaysAfter"]);
    }

    [Fact]
    public async Task RequestLeaveAsync_NeverWritesToDatabase()
    {
        // Rule 3 compliance check: plugin must not call SaveChanges.
        var countBefore = await _dbContext.LeaveRequests.CountAsync();
        await _plugin.RequestLeaveAsync(FutureStart, FutureEnd, "Annual", "Test leave");
        var countAfter  = await _dbContext.LeaveRequests.CountAsync();
        Assert.Equal(countBefore, countAfter);
    }

    [Fact]
    public async Task RequestLeaveAsync_StartAfterEnd_ReturnsToolError()
    {
        var json     = await _plugin.RequestLeaveAsync(FutureEnd, FutureStart, "Annual", "Inverted dates");
        var envelope = JsonSerializer.Deserialize<ToolEnvelope>(json, CamelCaseOptions);
        var error    = Assert.IsType<ToolError>(envelope);
        Assert.Equal("INVALID_DATES", error.Code);
        Assert.False(error.Retryable);
    }

    private void SeedTestData()
    {
        _dbContext.Employees.Add(new Employee(1, "Test User", "test@example.com", "Engineering", null, 30m));
        _dbContext.SaveChanges();
        _dbContext.LeaveAllocations.Add(new LeaveAllocation(0, 1, LeaveType.Annual, 20, DateTime.UtcNow.Year));
        _dbContext.SaveChanges();
    }

    private static readonly JsonSerializerOptions CamelCaseOptions = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
```

**Key assertions to include in every plugin test:**

- `Assert.IsType<WriteProposal>()` or `Assert.IsType<ReadResult>()` — verify the envelope type
- Provenance sources per field — verify `UserStated` vs `Computed` per field
- Rule 3 compliance — count rows before and after; write plugins must not change the count
- Error code checks — verify `ToolError.Code` and `Retryable` for each error path

**For read tool tests**, the pattern is identical — deserialize to `ReadResult`, verify `Entities` count, field names, and markdown content.

**Testing a ContextExtractor in isolation:**

For extractors that only call `EmitEntity` (the common case), the read plugin integration test is sufficient. For extractors that also record field-level provenance via `ContextFabric.SetFieldChain`, expose a `public ProcessEntity(EntityRef entity)` method (see Section 4, *Testability*) and call it directly:

```csharp
// From (the private Sakwala/affiant-host-apps repo): apps/HRPortal/tests/HRPortal.Api.Tests/Agent/Integration/LeaveBalanceExtractorTests.cs
var fabric    = new ContextFabric();
var extractor = new LeaveBalanceExtractor(fabric, NullLogger<LeaveBalanceExtractor>.Instance);

var result = GetReadResult(await _plugin.GetLeaveBalance(employeeId: 1));
extractor.ProcessEntity(result.Entities[0]);

Assert.Equal("Employee", fabric.Snapshot()["1"].EntityType);
Assert.Equal(ProvenanceSource.Computed, fabric.GetFieldChain("Employee:1:AnnualRemainingDays")!.Current.Source);
```

Full SK pipeline wiring (registering the filter with a `Kernel` and calling via `OnFunctionInvocationAsync`) is needed only when testing that `MatchesTool` correctly skips other tools' results.

---

## 8. Appendix: Quick Reference

### Minimal read tool

```csharp
public class MyReadPlugin(IServiceScopeFactory scopeFactory)
{
    [KernelFunction, Description("...")]
    public async Task<string> SearchEntities(
        [Description("...")] string? filter = null,
        CancellationToken ct = default)
    {
        try
        {
            // MyDbContext is Scoped; every SK plugin is effectively a singleton (Section 2,
            // affiant#21) — resolve it per invocation, never via constructor injection.
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MyDbContext>();

            var items = await dbContext.Entities.AsNoTracking()
                .Where(e => filter == null || e.Name.Contains(filter))
                .Take(50).ToListAsync(ct);

            var entities = items.Select(x => new EntityRef(
                "MyEntity", x.Id.ToString(), x.Name,
                new Dictionary<string, object> { ["Name"] = x.Name })).ToArray();

            var md = string.Join("\n", items.Select(x => $"- {x.Name} (ID: {x.Id})"));
            return new ReadResult("SearchEntities", DateTimeOffset.UtcNow,
                $"Found {items.Count}", md, entities).ToJsonString();
        }
        catch (Exception ex) when (ex is TimeoutException or DbUpdateException)
        {
            return new ToolError("SearchEntities", DateTimeOffset.UtcNow,
                "DB_TIMEOUT", "Temporarily unavailable.", true).ToJsonString();
        }
    }
}
```

### Minimal write tool

```csharp
// If this tool needs to read a Scoped dependency (e.g. MyDbContext, for a lookup before
// building the proposal), inject IServiceScopeFactory and resolve it per invocation — see
// Section 2 (affiant#21). This example needs no such dependency, so none is shown.
public class MyWritePlugin
{
    [KernelFunction, Description("...")]
    public async Task<string> CreateEntity(
        [Description("...")] string name,
        CancellationToken ct = default)
    {
        const string toolName = "CreateEntity";
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return new ToolError(toolName, DateTimeOffset.UtcNow,
                    "INVALID_NAME", "Name cannot be empty.", false).ToJsonString();

            var fields = new AffidavitField[]
            {
                new("Name", name, null, ProvenanceChain.From(ProvenanceTag.FromUser("Name"))),
            };

            return new WriteProposal(toolName, DateTimeOffset.UtcNow,
                new Affidavit("create", "MyEntity", null, fields, 1.0f, [], true))
                .ToJsonString();
        }
        catch (Exception ex) when (ex is TimeoutException or DbUpdateException)
        {
            return new ToolError(toolName, DateTimeOffset.UtcNow,
                "DB_TIMEOUT", "Temporarily unavailable.", true).ToJsonString();
        }
    }
}
```

### Minimal ContextExtractor

```csharp
public class MyEntityExtractor(ContextFabric fabric, ILogger<MyEntityExtractor> logger)
    : ContextExtractor(fabric, logger)
{
    protected override bool MatchesTool(string name) =>
        name.Equals("SearchEntities", StringComparison.OrdinalIgnoreCase);

    protected override Task ExtractAsync(ReadResult result, FunctionInvocationContext ctx)
    {
        foreach (var entity in result.Entities) EmitEntity(entity);
        return Task.CompletedTask;
    }
}
```

### Minimal error return

```csharp
return new ToolError(toolName, DateTimeOffset.UtcNow,
    "ENTITY_NOT_FOUND", $"No entity with ID {id}.", false).ToJsonString();
```

---

## 9. Common Mistakes and Debugging

### 1. "I returned the tool result, but the LLM can't reference entities in the next turn"

**Symptom:** LLM says "I found the employee" but can't recall details one turn later.

**Cause:** You didn't implement a `ContextExtractor` for that read tool.

**Fix:** For every read tool that returns `EntityRef[]` with meaningful entities, create a `ContextExtractor` subclass, override `MatchesTool` and `ExtractAsync`, and register it as `IFunctionInvocationFilter`.

---

### 2. "My write tool works, but no Evidence Card appears"

**Symptom:** `WriteProposal` is returned correctly, but the review flow is skipped.

**Cause:** `Affidavit.RequiresConfirmation` is `false`, or your `IApprovalPolicy` auto-approves silently.

**Fix:** Set `RequiresConfirmation: true` unless you explicitly want auto-approval. Check your `IApprovalPolicy` implementation — `StandingOrderPolicy` and `ReviewerConfirmationPolicy` have distinct bypass conditions.

---

### 3. "The Affidavit has fields but the Evidence Card shows grey provenance badges"

**Symptom:** Fields appear in the review UI but provenance source shows as `Empty`.

**Cause:** You constructed `AffidavitField` without a meaningful `ProvenanceChain`, or used `ProvenanceSource.Empty` when the real source is known.

**Fix:** Tag every field with its actual source. Use `ProvenanceTag.FromUser(fieldName)` for user-stated values, `new ProvenanceTag(Computed, 1.0f, "evidence string", null)` for computed values. The `Evidence` string is displayed to the reviewer — make it meaningful.

---

### 4. "I'm getting 'DI resolution failed for IServiceScopeFactory'"

**Symptom:** Plugin constructor throws on `IServiceScopeFactory` resolution.

**Cause:** You're injecting a scoped service (like `DbContext`) directly into a singleton plugin.

**Fix:** Inject `IServiceScopeFactory` instead and call `scopeFactory.CreateScope()` per invocation. `IServiceScopeFactory` is registered automatically by the host — you don't need to add it manually.

---

### 5. "My field mapper throws because a field is missing"

**Symptom:** `InvalidOperationException: Affidavit missing required field 'StartDate'`

**Cause:** The field names in your `AffidavitField` array don't match the names you look up in `MapFromAffidavit`.

**Fix:** Field names are case-sensitive string keys. Ensure the name used in `new AffidavitField("StartDate", ...)` exactly matches `fieldDict["StartDate"]` in `MapFromAffidavit`. A shared constant or enum avoids this class of error entirely.

---

### 6. "My field mapper gets the wrong type on Value"

**Symptom:** `AffidavitField.Value` is a `string` but domain model expects `DateTime`.

**Cause:** `AffidavitField.Value` is typed as `object?` and JSON deserialization returns strings.

**Fix:** Always call `Value?.ToString()` and then parse to the target type explicitly (`DateTime.Parse`, `Enum.Parse`, `int.Parse`). Use `TryParse` variants to handle user input gracefully without throwing.

---

### 7. "The test passes but the plugin fails in production"

**Symptom:** Integration test succeeds; production throws `ObjectDisposedException` on `DbContext`.

**Cause:** Your test instantiates `DbContext` directly, but production code uses a scoped DI lifetime. The plugin is holding a reference to a `DbContext` that gets disposed between requests.

**Fix:** Use the scope factory pattern (see Section 2 — this is the default pattern for any `Scoped` plugin dependency, not a situational alternative, as of the affiant#21 correction) so each invocation creates and disposes its own scope.

---

### 8. "The LLM always provides a value for my nullable parameter"

**Symptom:** Plugin has `string? email = null` but the LLM always passes something.

**Cause:** The LLM infers from context that it should provide the field, even when optional.

**Fix:** Make the optionality explicit in the `[Description]` attribute: `[Description("Email to match (optional; omit if not filtering by email)")]`. Ensure the plugin handles `null` correctly for every nullable parameter.

---

## 10. Naming LLM-Visible Tools: SK vs MAF

**Every tool your plugin exposes has two names that can drift apart: the C# method name, and the
name the LLM actually sees.** By default they're the same. Both backends let you override the
LLM-visible name independently of the method name — and the Area 2 architecture review (gate
ruling 2, "C-prime") standardizes *how*: the override should come from a `public const string`
member of a per-host `ToolNames` class, never a bare string literal, so the declaration site and
every other reference to that tool's name (prompts, telemetry, provenance tags, extractor
matching) share one compiler-checked symbol. Renaming the constant then updates every call site at
compile time; changing its value is a deliberate one-line diff.

**Semantic Kernel:**

```csharp
public class RequestLeavePlugin
{
    [KernelFunction(ToolNames.RequestLeave)]   // e.g. "request_leave"
    [Description("Propose a leave request...")]
    public async Task<string> RequestLeaveAsync(/* ... */) { /* ... */ }
}
```

`[KernelFunction]`'s constructor already accepts a `string`, so this needs no framework-side
mechanism — just the discipline of feeding it a constant instead of a literal.

**Microsoft Agent Framework:** `AffiantToolCatalog.FromType<T>()` has no `[KernelFunction]`-style
marker at all (it reflects every public method — see the adapter guide,
`docs/adapters/microsoft-agent-framework.md` §4), so before
[affiant#16](https://github.com/Sakwala/affiant/issues/16) there was no way to give a tool an
LLM-visible name different from its C# method name — hosts had to rename the *method itself* to
the desired LLM-visible spelling, which works but fights normal C# naming conventions.
`Affiant.AgentFramework.Attributes.AffiantToolNameAttribute` closes the gap:

```csharp
public sealed class ThingPlugin
{
    [AffiantToolName(ToolNames.SearchThing)]   // e.g. "search_thing"
    public string SearchThing(string query) { /* ... */ }
}
```

A method with no `[AffiantToolName]` gets its LLM-visible name from `AIFunctionFactory.Create`'s
default resolution — normally the bare C# method name, but `AIFunctionFactory.Create` strips a
trailing `Async` when the method returns `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, or
`IAsyncEnumerable<T>` (the same condition SK's `[KernelFunction]` walker uses). A
`Task<string> FetchThingAsync()` method with no override surfaces as tool `FetchThing`, not
`FetchThingAsync` — see `docs/adapters/microsoft-agent-framework.md` §4 for the full behavior and
why relying on it is discouraged. `AffiantToolDescriptor.FunctionName` always mirrors this
resolved name exactly, whatever it turns out to be — that invariant, not "the bare method name,"
is what `FromType<T>()` guarantees. Applying `[AffiantToolName]` with a null/blank name, or in a
way that makes two methods resolve to the same effective name, throws `InvalidOperationException`
at catalog-build time — loud, not silent.

**Verifying the discipline held.** Hand-rolling a reflection test per host (enumerate every
`[KernelFunction]`/tool method, assert its effective name is a `ToolNames` member, assert every
`ToolNames` member maps to exactly one tool) works but is easy to under-maintain. Prefer
`Affiant.Testing.ComplianceHarness.ComplianceHarness.AssertToolNameRegistryParity(toolNamesType,
exposedToolNames)` (see that package's README) — it asserts the same bijection from one shared,
tested implementation, given the one adapter-specific reflection step (SK: `[KernelFunction].Name`;
MAF: `AffiantToolCatalog.Descriptors[].FunctionName`) that only your host code can perform.

---

*Prose: ~11 pages (~2,800 words). Code: ~700 lines (~12 pages at 60 lines/page). Total with code: ~23 pages, within the 30-page limit. Prose alone is digestible in 30 minutes.*

*v1.1 additions (Story 13.2 feedback): host folder convention note (§4), extractor testability pattern (§4), marker interface clarification (§5.1), extending an existing IWriteExecutor walkthrough (§5.3), EF migration guidance (§5.3), ContextExtractor isolation test example (§7).*

*v1.2 addition (Area 2 P2, 2026-08-02): naming LLM-visible tools, SK vs MAF, and the ToolNames/FabricKeys parity-check pattern (§10).*
