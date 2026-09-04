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

## Expiry

An entry past its `ExpiresAt` reads as `Expired` — from `GetDocketEntryAsync`, and by its absence from `ListPendingAsync` — whether or not the sweep has reached it, on an inclusive boundary (at `ExpiresAt` the entry is expired). Expiry is a **state**, not an event: there is no background job to be down and no window in which a lapsed entry is still decidable because nobody swept it yet. The sweep does the durable work: it commits the transition, broadcasts `DocketExpired`, and leaves the persisted state a resubmission can act on.

`DocketExpiryService` owns a schedule; the **store** owns the sweep. Each tick calls `IDocketStore.ExpireDueAsync(now, scope, limit)` — which finds the due rows, commits their transitions under one guard, and reports whether more remain — until the store says no more remain or the tick's own cap is reached. So a tick is bounded twice, and a backlog larger than the product drains over the ticks that follow:

```csharp
builder.Services.AddAffiantDocket(docket =>
{
    docket.UseInMemory();
    docket.ExpirySweepBatchSize = 500;        // rows per store call — default: 100
    docket.ExpirySweepBatchesPerTick = 4;     // store calls per tick   — default: 10
});
```

A deployment that partitions its Docket — one process per tenant, one worker per region — narrows what its sweep reaches so two processes never contend for the same rows:

```csharp
builder.Services.AddAffiantDocket(docket => docket.SweepScope = DocketScope.Tenant(tenantId));
```

A host that would rather schedule the sweep itself — a serverless deployment with no long-lived process, a cron entry, a queue worker — does not register `AddAffiantDocket`'s hosted service at all and calls `ExpireDueAsync` on its own cadence. No framework package owns a timer that expiry depends on.

## Retention, purge and export

How long an approval record must be kept is a legal question with a different answer in every jurisdiction the gate runs in, so the store exposes the operations and the host drives them:

```csharp
// Age out terminal rows, in bounded steps, until nothing is left to remove.
var policy = new DocketRetentionPolicy(DateTimeOffset.UtcNow.AddYears(-7));
RetentionResult result;
do
{
    result = await store.ApplyRetentionAsync(policy, DocketScope.Tenant(tenantId), limit: 500, ct);
}
while (result.More);

await store.PurgeTenantAsync(tenantId, ct);            // a tenant's data, on demand, all of it

await foreach (var entry in store.ExportAsync(DocketScope.Tenant(tenantId), ct))
    await sink.WriteAsync(entry, ct);                  // streamed, never materialised
```

**Retention never ages out an `Approved` row whose write has not been reported**, however old. It is the only record that a write was authorised and has not happened, and no policy may remove it.

Everything above reads the clock through the `TimeProvider` in DI. `AddAffiantCore` registers `TimeProvider.System`; register your own before it (a `FakeTimeProvider` in a test) and the stores, the gate and the sweep all move with it.

Registration order between `AddAffiantCore`, `AddAffiantDocket` and `AddAffiantEntityFramework` does not matter. A host that ends up with no `IDocketStore` registered at all fails at **startup** with a message naming the missing registration and the package that provides it (`AddAffiantCore`'s wire-up validator) — not silently at its first write.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.Docket.Stores` | `InMemoryDocketStore` — the process-local `IDocketStore`. The SQLite/PostgreSQL implementations live in `Affiant.EntityFramework` |
| `Affiant.Docket.Services` | `DocketExpiryService` — the background sweep that transitions lapsed-TTL entries to `Expired` and re-broadcasts still-`Pending` Evidence Cards; backend-neutral, resolves `IDocketStore` per tick |
| `Affiant.Docket.Options` | `DocketOptions` — the store-selection builder for `AddAffiantDocket` (`UseInMemory()`, `ExpirySweepBatchSize`); `AffiantDocketOptions` — the sweep's runtime knobs |
| `Affiant.Docket.Extensions` | `ServiceCollectionExtensions` — `AddAffiantDocket` |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the review-queue and expiry semantics
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
