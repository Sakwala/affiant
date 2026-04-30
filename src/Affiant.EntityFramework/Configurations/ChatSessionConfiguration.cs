using Affiant.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Affiant.EntityFramework.Configurations;

public class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSessionEntity>
{
    public void Configure(EntityTypeBuilder<ChatSessionEntity> builder)
    {
        builder.ToTable("ChatSessions");
        builder.HasKey(e => e.SessionId);
        builder.HasIndex(e => new { e.TenantId, e.UserId });
    }
}
