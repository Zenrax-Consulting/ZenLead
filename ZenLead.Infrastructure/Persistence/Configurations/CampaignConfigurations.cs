using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence.Configurations;

public class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> builder)
    {
        builder.Property(c => c.Name).HasMaxLength(200);
        builder.Property(c => c.FromName).HasMaxLength(100);
        builder.Property(c => c.TimeZoneId).HasMaxLength(64);
        builder.HasIndex(c => new { c.WorkspaceId, c.Status });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(c => c.Steps).WithOne().HasForeignKey(s => s.CampaignId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class CampaignStepConfiguration : IEntityTypeConfiguration<CampaignStep>
{
    public void Configure(EntityTypeBuilder<CampaignStep> builder)
    {
        builder.Property(s => s.SubjectTemplate).HasMaxLength(300);
        builder.Property(s => s.BodyTemplate);                                   // nvarchar(max)
        builder.HasIndex(s => new { s.CampaignId, s.Order }).IsUnique();
        builder.HasIndex(s => s.WorkspaceId);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(s => s.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CampaignEnrollmentConfiguration : IEntityTypeConfiguration<CampaignEnrollment>
{
    public void Configure(EntityTypeBuilder<CampaignEnrollment> builder)
    {
        builder.Property(e => e.FailureReason).HasMaxLength(500);
        builder.HasIndex(e => new { e.CampaignId, e.LeadId }).IsUnique();
        builder.HasIndex(e => new { e.Status, e.NextSendAt });                 // the sender's "due now" query (F23)
        builder.HasIndex(e => new { e.WorkspaceId, e.LeadId });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(e => e.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Campaign>().WithMany().HasForeignKey(e => e.CampaignId).OnDelete(DeleteBehavior.Cascade);
        // soft-deleted leads keep their campaign history; a hard delete of an enrolled lead is refused
        builder.HasOne(e => e.Lead).WithMany().HasForeignKey(e => e.LeadId).OnDelete(DeleteBehavior.Restrict);
    }
}
