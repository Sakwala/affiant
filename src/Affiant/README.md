# Affiant

Meta-package for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

This package carries no code. Installing it installs the nine co-versioned Affiant runtime packages, which target `net10.0`:

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

The framework's tenth package, `Affiant.Testing.ComplianceHarness`, is not a dependency of this one.
It exists for a test project's provenance assertions, and installing it into the host it is meant to
test would put the harness and its assets in the host's runtime graph. Add it to your test project
directly: `dotnet add package Affiant.Testing.ComplianceHarness --prerelease`.

## Install

```
dotnet add package Affiant --prerelease
```

## The individual packages remain the fine-grained choice

The framework's packages are a strict DAG rooted at `Affiant.Abstractions`, and no scenario in the
framework's own install table needs all nine: a Semantic Kernel host wants six, a SQL-backed one
seven, and a project implementing a contract only wants `Affiant.Abstractions`. Reach for this
meta-package when you want the whole runtime framework in one command — a spike, a sample, a
reference host — and install the individual packages when you want only what your scenario needs.
The framework's
[README](https://github.com/Sakwala/affiant/blob/main/README.md#which-of-the-10-packages-do-i-need)
lists the packages per scenario.

Two adapters are never wired over the same tool catalog: pick one of `Affiant.SemanticKernel`,
`Affiant.AgentFramework` or `Affiant.Extensions.AI` for a given catalog, whichever your host already
uses. Installing this package installs all three; it does not wire any of them.

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
