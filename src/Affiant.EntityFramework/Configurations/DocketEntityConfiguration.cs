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

        // The later facts. Every one is nullable: a freshly filed row has none of them, and a
        // column that forced a value would be inventing a fact the row does not yet hold.
        builder.Property(e => e.ToolName).IsRequired(false);
        builder.Property(e => e.Execution).IsRequired(false);
        builder.Property(e => e.ExecutionDetail).IsRequired(false);
        builder.Property(e => e.DecisionJson).IsRequired(false);
        builder.Property(e => e.AttestationJson).IsRequired(false);
        builder.Property(e => e.BlockedJson).IsRequired(false);
        builder.Property(e => e.CompositeRef).IsRequired(false);
        builder.Property(e => e.AmendedAffidavitJson).IsRequired(false);
        builder.Property(e => e.AmendedProvenanceChainsJson).IsRequired(false);
        builder.Property(e => e.PreservedAmendmentsJson).IsRequired(false);
        builder.Property(e => e.Supersedes).IsRequired(false);
        builder.Property(e => e.DecidedAt).IsRequired(false);
        builder.Property(e => e.ProtocolVersion)
            .IsRequired()
            .HasDefaultValue(Affiant.Abstractions.Models.AffiantProtocol.Version);
        builder.Property(e => e.CreatedAtTicks).IsRequired().HasDefaultValue(0L);
        builder.Property(e => e.ExpiresAtTicks).IsRequired().HasDefaultValue(0L);
        builder.Property(e => e.DecidedAtTicks).IsRequired(false);

        builder.HasIndex(e => new { e.TenantId, e.Status });
        builder.HasIndex(e => new { e.SessionId, e.Status });
        builder.HasIndex(e => e.ResubmittedTo);
        builder.HasIndex(e => e.Supersedes);

        // The two indexes the bounded reads need. The sweep asks for pending rows in deadline order
        // and the paged listings ask for rows in filing order, both scoped to a tenant; without
        // these each is a scan, and a scan is how a "bounded" query quietly stops being one.
        builder.HasIndex(e => new { e.Status, e.ExpiresAtTicks });
        builder.HasIndex(e => new { e.TenantId, e.Status, e.CreatedAtTicks });
        builder.HasIndex(e => new { e.Status, e.DecidedAtTicks });
    }
}
