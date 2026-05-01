using Affiant.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;

namespace Affiant.EntityFramework;

public class AffiantDbContext(DbContextOptions<AffiantDbContext> options) : DbContext(options)
{
    public const string DefaultSchema = "affiant";
    private const string _schemaName = DefaultSchema;

    public DbSet<ChatSessionEntity> ChatSessions => Set<ChatSessionEntity>();
    public DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();
    public DbSet<ConversationContextEntity> ConversationContexts => Set<ConversationContextEntity>();
    public DbSet<DocketEntryEntity> Docket => Set<DocketEntryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AffiantDbContext).Assembly);

        // Postgres-specific column types for JSON columns.
        // Using jsonb gives GIN-indexable binary JSON on Postgres; other providers
        // (SQLite) fall back to the default text mapping via EnsureCreated.
        if (Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            modelBuilder.Entity<ChatMessageEntity>()
                .Property(e => e.ArgumentsJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<ChatMessageEntity>()
                .Property(e => e.MetadataJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<ConversationContextEntity>()
                .Property(e => e.EntitiesJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<ConversationContextEntity>()
                .Property(e => e.FieldValuesJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<ConversationContextEntity>()
                .Property(e => e.ProvenanceChainsJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<DocketEntryEntity>()
                .Property(e => e.AffidavitJson)
                .HasColumnType("jsonb")
                .HasColumnName("Affidavit");

            modelBuilder.Entity<DocketEntryEntity>()
                .Property(e => e.ProvenanceChainsJson)
                .HasColumnType("jsonb")
                .HasColumnName("ProvenanceChains");

            modelBuilder.Entity<DocketEntryEntity>()
                .Property(e => e.AmendmentsJson)
                .HasColumnType("jsonb")
                .HasColumnName("Amendments");
        }

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            entityType.SetSchema(_schemaName);
        }
    }
}
