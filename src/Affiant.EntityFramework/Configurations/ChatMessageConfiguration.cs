using Affiant.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Affiant.EntityFramework.Configurations;

public class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessageEntity>
{
    public void Configure(EntityTypeBuilder<ChatMessageEntity> builder)
    {
        builder.ToTable("ChatMessages");
        builder.HasKey(e => e.MessageId);
        builder.HasIndex(e => new { e.SessionId, e.Ordinal });
        builder.HasOne<ChatSessionEntity>()
               .WithMany()
               .HasForeignKey(e => e.SessionId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.Property(e => e.ArgumentsJson).HasColumnName("Arguments");
        builder.Property(e => e.MetadataJson).HasColumnName("Metadata");
    }
}
