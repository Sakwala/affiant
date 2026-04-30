using Affiant.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Affiant.EntityFramework.Configurations;

public class ConversationContextConfiguration : IEntityTypeConfiguration<ConversationContextEntity>
{
    public void Configure(EntityTypeBuilder<ConversationContextEntity> builder)
    {
        builder.ToTable("ConversationContexts");
        builder.HasKey(e => e.SessionId);
        builder.HasOne<ChatSessionEntity>()
               .WithMany()
               .HasForeignKey(e => e.SessionId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.Property(e => e.EntitiesJson).HasColumnName("Entities");
        builder.Property(e => e.FieldValuesJson).HasColumnName("FieldValues");
        builder.Property(e => e.ProvenanceChainsJson).HasColumnName("ProvenanceChains");
    }
}
