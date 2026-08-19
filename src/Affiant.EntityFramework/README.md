# Affiant.EntityFramework

EntityFramework Core persistence adapter for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Provides the shared `AffiantDbContext` (chat sessions, conversation context, docket entries — schema-isolated so it can coexist with a host's own `DbContext`), SQLite and PostgreSQL `IChatSessionStore` implementations, an in-memory one with no EF dependency at all, and the EF Core migrations both SQL backends run against.

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

`Affiant.Docket`'s SQLite/PostgreSQL `IDocketStore` implementations share this same `AffiantDbContext` and migration set — register `Affiant.EntityFramework` alongside `Affiant.Docket` when using either SQL provider for the Docket.

## Package contents

| Namespace | Purpose |
|---|---|
| (root) | `AffiantDbContext`, `AffiantDbContextFactory` (design-time factory for `dotnet ef`) |
| `Affiant.EntityFramework.Stores` | `IChatSessionStore` implementations — `InMemoryChatSessionStore`, `SqliteChatSessionStore`, `PostgresChatSessionStore` |
| `Affiant.EntityFramework.Models` | EF entity types — `ChatSessionEntity`, `ChatMessageEntity`, `ConversationContextEntity`, `DocketEntryEntity` |
| `Affiant.EntityFramework.Configurations` | `IEntityTypeConfiguration<T>` implementations for each entity |
| `Affiant.EntityFramework.Migrations` | Checked-in EF Core migrations plus `AffiantMigrator` — the schema-apply/heal entry point hosts call at startup |
| `Affiant.EntityFramework.Extensions` | `ServiceCollectionExtensions` — `AddAffiantEntityFramework` |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the chat-session store's write-class contract (`AppendMessagesAsync` vs `SaveMessagesAsync`)
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
