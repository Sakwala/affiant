---
title: DI Registration Order — Rules the Container Won't Enforce For You
version: 1.0
date: 2026-08-20
status: current as of 1.0.0-beta.1 (framework main); documents two rules that exist only as
  conventions, not as anything the framework or a validator checks
scope: Hosts registering their own filters (`IToolInvocationFilter`, or Semantic-Kernel-specific
  `IAutoFunctionInvocationFilter`) alongside Affiant's `Add*` calls
audience: Adopters wiring a custom filter — read `docs/tool-authoring-guide.md` first if you have
  not written an Affiant tool or filter before; this page assumes you know what a
  `WriteProposal`, `Affidavit`, and `[AffiantWriteTool]` are
related:
  - README.md "Mandatory vs optional wiring" — where these calls sit in the bigger picture
  - src/Affiant.Core/Services/ToolInvocationPipeline.cs (Rule 1's mechanism)
  - src/Affiant.SemanticKernel/Filters/AffiantFilterPipeline.cs (Rule 2's mechanism)
---

# DI Registration Order

Most ASP.NET Core DI registrations are order-independent — the container resolves a single
service the same way no matter which line registered it. Affiant's filter pipelines are the
exception: they are **ordered lists**, and the .NET container's own convention for a
multiply-registered interface (`services.GetServices<T>()`, or Semantic Kernel's own filter
collections) returns registrations **in the order they were added**, first-registered running
outermost. Two concrete rules follow from that mechanism. Both were learned empirically, in
production host applications, before either was written down anywhere — this page is that missing
documentation.

**Neither rule is checked by any startup validator.** Get one wrong and every part of the
framework that *does* validate startup (`AddAffiantCore()`'s wire-up validator,
`AddAffiantSemanticKernel()`'s `AffiantStartupValidator`, MAF's hosted-tool audit — see
README.md's "Mandatory vs optional wiring" table) passes cleanly. The host starts, serves traffic,
and holds normal conversations. The only symptom is that your filter runs at the wrong position in
the pipeline relative to Affiant's own filters — which, depending on what your filter does, may
never be visibly wrong until the one call that depended on the correct position.

## Rule 1 — a filter that must wrap the whole Affiant pipeline registers *before* `AddAffiantCore()`

**Applies to:** any host-authored `IToolInvocationFilter` (`Affiant.Abstractions.Interfaces`) —
the backend-neutral filter contract both the Semantic Kernel and Microsoft Agent Framework bridges
funnel every tool invocation through.

**Mechanism.** `Affiant.Core.Services.ToolInvocationPipeline` resolves the filter chain with
`serviceProvider.GetServices<IToolInvocationFilter>()`
(`src/Affiant.Core/Services/ToolInvocationPipeline.cs:52`) and runs it in the order returned.
`IServiceCollection`'s default DI container returns multiply-registered services in registration
order — the first `AddScoped<IToolInvocationFilter, T>()` call in your `Program.cs` produces the
**outermost** filter (the one whose "before" code runs first and whose "after" code runs last, if
your filter wraps the call). `AddAffiantCore()` registers Affiant's own neutral-pipeline filters
(provenance tagging, the deterministic short-circuit, tracing) as part of its own call. Anything
you register *after* `AddAffiantCore()` lands **inside** those filters, not around them.

```csharp
// Correct: MyFilter wraps everything Affiant does, including its internal short-circuit.
builder.Services.AddScoped<IToolInvocationFilter, MyFilter>();
builder.Services.AddAffiantCore();

// Wrong, no exception: MyFilter now runs nested inside Affiant's own filters instead of
// wrapping them — it will not see calls Affiant's own short-circuit intercepts internally.
builder.Services.AddAffiantCore();
builder.Services.AddScoped<IToolInvocationFilter, MyFilter>();
```

**When this matters.** Any filter that needs to see *every* tool invocation unconditionally —
establishing a per-invocation ambient scope another Scoped service in the same call needs to
resolve, for example — needs to be outermost. A filter that only cares about specific tool calls
Affiant's own filters already let through is less sensitive to this, but registering before
`AddAffiantCore()` is always the safe default if you are not sure.

## Rule 2 — a Semantic-Kernel host's outermost auto-invocation filter registers *before* `AddAffiantSemanticKernel()`

**Applies to:** SK hosts only, and only to a host-authored `Microsoft.SemanticKernel.IAutoFunctionInvocationFilter`
— the SK-native filter interface (distinct from Rule 1's backend-neutral `IToolInvocationFilter`)
that fires at Semantic Kernel's own auto-invocation loop, where result replacement and
conversation termination live.

**Mechanism.** `Kernel` populates `Kernel.AutoFunctionInvocationFilters` from DI in registration
order, the same first-registered-is-outermost rule as Rule 1.
`AddAffiantSemanticKernel()` → `AddAffiantSkFilters()` registers Affiant's own
`AffiantAutoFunctionInvocationBridge` as an `IAutoFunctionInvocationFilter`
(`src/Affiant.SemanticKernel/Filters/AffiantFilterPipeline.cs:50`) — the bridge that carries
`TaskInferenceMergeFilter` and `ReviewGateFilter` (the neutral pipeline's steps 6 and 7). A host
filter registered *after* `AddAffiantSemanticKernel()` lands at a later index than the bridge, not
ahead of it.

```csharp
// Correct: MyAutoInvocationFilter is index 0, Affiant's bridge is index 1.
builder.Services.AddScoped<IAutoFunctionInvocationFilter, MyAutoInvocationFilter>();
builder.Services.AddAffiantSemanticKernel();

// Wrong, no exception: Affiant's bridge is now outermost instead of MyAutoInvocationFilter.
builder.Services.AddAffiantSemanticKernel();
builder.Services.AddScoped<IAutoFunctionInvocationFilter, MyAutoInvocationFilter>();
```

**When this matters.** Any host filter that needs to run before Affiant's review-gate/merge
logic sees a completed tool result — for example, a filter that must be the one to decide whether
a write proposal is even eligible for review before `ReviewGateFilter` files it.

## What *is* enforced today, so you don't have to guess

As of `1.0.0-beta.1`, two startup validators exist and neither of them checks either rule above —
worth stating explicitly so you don't assume a clean startup means your filter order is correct:

| Validator | Package | Checks | Does **not** check |
|---|---|---|---|
| `AffiantWireUpValidator` | `Affiant.Core` (registered by `AddAffiantCore()`) | That *some* package registered `IStreamingTransport` and `IDocketStore` before the host starts serving traffic | Registration order of anything, including these two rules |
| `AffiantStartupValidator` | `Affiant.SemanticKernel` (registered by `AddAffiantSemanticKernel()`) | Every `[KernelFunction]` has a matching `AffiantToolDescriptor`; every registered inference strategy resolves from DI | Filter registration order |
| Hosted-tool coverage audit | `Affiant.AgentFramework` (`WithAffiant(...)`) | That every tool on a wrapped MAF agent is either client-invoked or explicitly acknowledged as hosted/uncovered | Filter registration order (MAF has no `IAutoFunctionInvocationFilter` equivalent — Rule 2 is SK-only) |

If you get Rule 1 or Rule 2 wrong today, the fix is re-reading this page and re-ordering your
`Program.cs` — not reading an exception message, because there isn't one. If your project would
benefit from an automated check here, the mechanism each validator above already uses
(`IServiceCollection` inspected before `.Build()`, or an `IHostedService` inspecting the built
container) is the same shape a future analyzer for this would need; none exists yet.
