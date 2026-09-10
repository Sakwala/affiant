# Affiant

Meta-package for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

This package carries no code. Installing it installs all ten co-versioned Affiant packages, which target `net10.0`:

| Package | Purpose |
|---|---|
| `Affiant.Abstractions` | All primitive types and every framework interface. Reference this alone to implement a contract. |
| `Affiant.Core` | Concrete backend-neutral services: `ContextFabric`, `ReviewGate`, task-inference merge, the deterministic short-circuit, the tool-invocation pipeline, DI wiring. |
| `Affiant.SemanticKernel` | Semantic Kernel interception bridge. |
| `Affiant.AgentFramework` | Microsoft Agent Framework (MAF) interception bridge. |
| `Affiant.Extensions.AI` | Microsoft.Extensions.AI (M.E.AI) interception bridge. |
| `Affiant.Docket` | The review queue's backend-neutral half — `InMemoryDocketStore` plus the background expiry sweep. |
| `Affiant.EntityFramework` | EF Core persistence for sessions and dockets. |
| `Affiant.Policies` | Fluent approval policy graph — Standing Orders, Referrals, reviewer confirmation. |
| `Affiant.Transport.SignalR` | SignalR streaming transport and Evidence Card round-trip hub. |
| `Affiant.Testing.ComplianceHarness` | `ComplianceHarness.Verify(...)`, for your CI's provenance assertions. |

## Install

```
dotnet add package Affiant --prerelease
```

## The individual packages remain the fine-grained choice

The ten packages are a strict DAG rooted at `Affiant.Abstractions`, and no scenario in the framework's
own install table needs all ten: a Semantic Kernel host wants six, a SQL-backed one seven, a project
implementing a contract only wants `Affiant.Abstractions`, and `Affiant.Testing.ComplianceHarness`
belongs in a test project rather than the host. Reach for this meta-package when you want the whole
framework in one command — a spike, a sample, a reference host — and install the individual packages
when you want only what your scenario needs. The framework's
[README](https://github.com/Sakwala/affiant/blob/main/README.md#which-of-the-10-packages-do-i-need)
lists the packages per scenario.

Two adapters are never wired over the same tool catalog: pick one of `Affiant.SemanticKernel`,
`Affiant.AgentFramework` or `Affiant.Extensions.AI` for a given catalog, whichever your host already
uses. Installing this package installs all three; it does not wire any of them.

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
