using Affiant.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Affiant.EntityFramework.Configurations;

internal sealed class DocketEntityConfiguration : IEntityTypeConfiguration<DocketEntryEntity>
{
    public void Configure(EntityTypeBuilder<DocketEntryEntity> builder)
    {
        builder.ToTable("Docket");
        builder.HasKey(e => e.EntryId);

        builder.Property(e => e.SessionId).IsRequired();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.UserId).IsRequired();
        builder.Property(e => e.ReviewerUserId).IsRequired(false);
        builder.Property(e => e.OperationType).IsRequired();
        builder.Property(e => e.AffidavitJson).IsRequired().HasDefaultValue("{}");
        builder.Property(e => e.ProvenanceChainsJson).IsRequired().HasDefaultValue("{}");
        builder.Property(e => e.AmendmentsJson).IsRequired(false);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.ExpiresAt).IsRequired();
        builder.Property(e => e.Status).IsRequired().HasDefaultValue("Pending");
        builder.Property(e => e.ResubmittedTo).IsRequired(false);

        builder.HasIndex(e => new { e.TenantId, e.Status });
        builder.HasIndex(e => new { e.SessionId, e.Status });
        builder.HasIndex(e => e.ResubmittedTo);
    }
}
