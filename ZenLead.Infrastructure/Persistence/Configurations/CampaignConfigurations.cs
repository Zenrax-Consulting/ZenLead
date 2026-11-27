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

public class EmailMessageConfiguration : IEntityTypeConfiguration<EmailMessage>
{
    public void Configure(EntityTypeBuilder<EmailMessage> builder)
    {
        builder.Property(m => m.Subject).HasMaxLength(300);
        builder.Property(m => m.Body);                                          // nvarchar(max)
        builder.Property(m => m.Error).HasMaxLength(500);
        builder.Property(m => m.ProviderMessageId).HasMaxLength(200);
        builder.Property(m => m.SmtpMessageId).HasMaxLength(500);
        builder.HasIndex(m => new { m.EnrollmentId, m.StepId }).IsUnique();    // the double-send guard (F23)
        builder.HasIndex(m => new { m.WorkspaceId, m.CampaignId, m.SentAt });   // per-campaign stats (F28)
        builder.HasIndex(m => new { m.WorkspaceId, m.CampaignId, m.StepId, m.SentAt });   // per-step breakdown (F28)
        builder.HasIndex(m => new { m.WorkspaceId, m.QueuedAt });               // the daily caps
        builder.HasIndex(m => m.ProviderMessageId);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        // history survives: an enrollment with sent mail can't be hard-deleted
        builder.HasOne<CampaignEnrollment>().WithMany().HasForeignKey(m => m.EnrollmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class SuppressedEmailConfiguration : IEntityTypeConfiguration<SuppressedEmail>
{
    public void Configure(EntityTypeBuilder<SuppressedEmail> builder)
    {
        builder.Property(s => s.Email).HasMaxLength(256);
        builder.HasIndex(s => new { s.WorkspaceId, s.Email }).IsUnique();
        builder.HasOne<Workspace>().WithMany().HasForeignKey(s => s.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>F28: idempotency keys of applied Event Webhook events. Platform table (no WorkspaceId); F29 housekeeping prunes it.</summary>
public class ProcessedWebhookEventConfiguration : IEntityTypeConfiguration<ProcessedWebhookEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedWebhookEvent> builder)
    {
        builder.HasKey(p => p.SgEventId);
        builder.Property(p => p.SgEventId).HasMaxLength(ProcessedWebhookEvent.MaxIdLength);
        builder.HasIndex(p => p.ReceivedAt);                                    // housekeeping deletes by age
    }
}
