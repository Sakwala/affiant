# Affiant.EntityFramework

EntityFramework Core persistence adapter for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Provides the shared `AffiantDbContext` (chat sessions, conversation context, docket entries — schema-isolated so it can coexist with a host's own `DbContext`), the SQLite and PostgreSQL `IChatSessionStore` **and `IDocketStore`** implementations, an in-memory `IChatSessionStore` with no EF dependency at all, and the EF Core migrations every SQL backend runs against.

## Quick start

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantEntityFramework(ef =>
{
    ef.UsePostgres(connectionString); // or ef.UseSqlite(...) / ef.UseInMemory()
});
```

At startup, apply pending migrations (SQLite and PostgreSQL only — `UseInMemory()` needs no schema):

```csharp
await using var scope = app.Services.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<AffiantDbContext>();
await AffiantMigrator.MigrateAffiantSchemaAsync(db);
```

### The SQL-backed Docket lives here too (affiant#35)

`ef.UsePostgres(...)` / `ef.UseSqlite(...)` register **both** `IChatSessionStore` and `IDocketStore` for that provider. `SqliteDocketStore`/`PostgresDocketStore` moved into this package in 1.0.0-beta.1: they take this package's `AffiantDbContext` and map its `DocketEntryEntity` through migrations that always lived here, so keeping them in `Affiant.Docket` forced that package to depend on this one — an adapter-to-adapter dependency the framework's layering invariant forbids, and one that dragged EF Core plus both database drivers onto every `Affiant.Docket` consumer.

A SQL-backed host therefore wires:

```csharp
builder.Services.AddAffiantEntityFramework(ef => ef.UseSqlite(connectionString)); // IChatSessionStore + IDocketStore
builder.Services.AddAffiantDocket();                                              // DocketExpiryService (backend-neutral)
```

`ef.UseInMemory()` registers **no** `IDocketStore` — the in-memory implementation is `Affiant.Docket`'s `InMemoryDocketStore`, selected with `AddAffiantDocket(d => d.UseInMemory())`.

## Schema changes in this release

The Docket table gains the columns the review row needs to record what happened to a write, not only
that it was proposed: the execution outcome and its detail, the decision record, the attestation, the
blocked marker, the composite reference, the accepted amendment state, a refused late decision's
preserved amendments, the lineage's `Supersedes` half, the decision instant, the tool name and the
protocol tag — plus three sortable tick columns.

- **PostgreSQL** applies migration `AddDocketRowFacts` through `MigrateAffiantSchemaAsync` (or
  `dotnet ef database update`). It adds the columns and indexes and **backfills** the tick columns
  from the instants they mirror.
- **SQLite** has no migration history here — the checked-in migrations were generated under the
  Npgsql provider and map columns Npgsql's way, so running them against SQLite produces a table EF's
  own SQLite-mapped model cannot query. `AffiantMigrator` heals the drift instead: on every
  `MigrateAffiantSchemaAsync` it adds whichever of those columns the existing `Docket` table lacks,
  creates the indexes, and backfills the tick columns row by row. A future column addition is added
  to the list in `AffiantMigrator.HealSqliteDriftAsync` until a SQLite-native migration history
  exists.

**Why tick columns.** SQLite has no native `DateTimeOffset`: its EF provider stores one as ISO-8601
text and can translate neither an inequality nor an `ORDER BY` over it into SQL. Before these
columns, a paged listing or a bounded sweep on SQLite had to load every candidate row and filter in
memory — which is exactly what a bounded store contract must not do, and why the two backends had
begun to diverge. Both now query and order by the integer and return the `DateTimeOffset`, so they
page identically. The backfill matters: a pre-existing row left at the column default of zero would
read as filed and due at the beginning of time — expired the moment the sweep ran, and eligible for
retention immediately.

## Package contents

| Namespace | Purpose |
|---|---|
| (root) | `AffiantDbContext`, `AffiantDbContextFactory` (design-time factory for `dotnet ef`) |
| `Affiant.EntityFramework.Stores` | `IChatSessionStore` implementations — `InMemoryChatSessionStore`, `SqliteChatSessionStore`, `PostgresChatSessionStore` — and the SQL-backed `IDocketStore` implementations `SqliteDocketStore`, `PostgresDocketStore` |
| `Affiant.EntityFramework.Models` | EF entity types — `ChatSessionEntity`, `ChatMessageEntity`, `ConversationContextEntity`, `DocketEntryEntity` |
| `Affiant.EntityFramework.Configurations` | `IEntityTypeConfiguration<T>` implementations for each entity |
| `Affiant.EntityFramework.Migrations` | Checked-in EF Core migrations plus `AffiantMigrator` — the schema-apply/heal entry point hosts call at startup |
| `Affiant.EntityFramework.Extensions` | `ServiceCollectionExtensions` — `AddAffiantEntityFramework` |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the chat-session store's write-class contract (`AppendMessagesAsync` vs `SaveMessagesAsync`)
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
