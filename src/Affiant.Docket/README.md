# Affiant.Docket

Durable review queue for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Persists and expires `DocketEntry` review requests across three interchangeable backends (in-memory, SQLite, PostgreSQL) and runs the background expiry sweep that guarantees Evidence Cards reach a reviewer at least once, even across a dropped/reconnected SignalR connection.

## Quick start

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantDocket(docket =>
{
    docket.UsePostgres(connectionString); // or docket.UseSqlite(...) / docket.UseInMemory()
});
```

Exactly one provider must be selected — `AddAffiantDocket` throws at startup otherwise. The SQLite and PostgreSQL providers additionally require `Affiant.EntityFramework`'s `AddAffiantEntityFramework(...)` to be registered (they share its `AffiantDbContext` and migrations); `UseInMemory()` has no such dependency.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.Docket.Stores` | `IDocketStore` implementations — `InMemoryDocketStore`, `SqliteDocketStore`, `PostgresDocketStore` |
| `Affiant.Docket.Services` | `DocketExpiryService` — the background sweep that transitions lapsed-TTL entries to `Expired` and re-broadcasts still-`Pending` Evidence Cards |
| `Affiant.Docket.Options` | `DocketOptions` — the provider-selection builder for `AddAffiantDocket` |
| `Affiant.Docket.Extensions` | `ServiceCollectionExtensions` — `AddAffiantDocket` |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the review-queue and expiry semantics
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
