# Affiant.Docket

Durable review queue for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Holds the review queue's backend-neutral half: the process-local `IDocketStore` and the background expiry sweep that guarantees Evidence Cards reach a reviewer at least once, even across a dropped/reconnected SignalR connection. Installing this package pulls no database driver — it depends on `Affiant.Core` and `Affiant.Abstractions` only.

## Quick start

**In-memory Docket** (development, tests, single-process hosts — nothing survives a restart):

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantDocket(docket => docket.UseInMemory());
```

**SQL-backed Docket** (SQLite or PostgreSQL) — the store comes from `Affiant.EntityFramework`, this package still supplies the expiry sweep:

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantEntityFramework(ef => ef.UsePostgres(connectionString)); // registers IDocketStore + IChatSessionStore
builder.Services.AddAffiantDocket();                                                // registers DocketExpiryService
```

> **Changed in 1.0.0-beta.1 (affiant#35).** `DocketOptions.UseSqlite(...)`/`UsePostgres(...)` no longer exist. `SqliteDocketStore` and `PostgresDocketStore` take `Affiant.EntityFramework`'s `AffiantDbContext` and are mapped by entity configurations and migrations that already lived in that package, so both stores moved there and are registered by `AddAffiantEntityFramework` — the same shape `IChatSessionStore` always had. This removes `Affiant.Docket`'s dependency on `Affiant.EntityFramework`, so installing it no longer drags EF Core, the SQLite provider and Npgsql onto a host that only wants `InMemoryDocketStore`.

Registration order between `AddAffiantCore`, `AddAffiantDocket` and `AddAffiantEntityFramework` does not matter. A host that ends up with no `IDocketStore` registered at all fails at **startup** with a message naming the missing registration and the package that provides it (`AddAffiantCore`'s wire-up validator) — not silently at its first write.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.Docket.Stores` | `InMemoryDocketStore` — the process-local `IDocketStore`. The SQLite/PostgreSQL implementations live in `Affiant.EntityFramework` |
| `Affiant.Docket.Services` | `DocketExpiryService` — the background sweep that transitions lapsed-TTL entries to `Expired` and re-broadcasts still-`Pending` Evidence Cards; backend-neutral, resolves `IDocketStore` per tick |
| `Affiant.Docket.Options` | `DocketOptions` — the store-selection builder for `AddAffiantDocket` (`UseInMemory()`) |
| `Affiant.Docket.Extensions` | `ServiceCollectionExtensions` — `AddAffiantDocket` |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the review-queue and expiry semantics
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
