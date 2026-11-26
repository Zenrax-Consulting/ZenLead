using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence.Configurations;

public class InboxThreadConfiguration : IEntityTypeConfiguration<InboxThread>
{
    public void Configure(EntityTypeBuilder<InboxThread> builder)
    {
        builder.Property(t => t.Subject).HasMaxLength(500);
        builder.HasIndex(t => t.EnrollmentId).IsUnique().HasFilter("[EnrollmentId] IS NOT NULL");   // one thread per enrollment
        builder.HasIndex(t => new { t.WorkspaceId, t.LastMessageAt });
        builder.HasIndex(t => new { t.WorkspaceId, t.IsRead });
        builder.HasIndex(t => new { t.WorkspaceId, t.Classification });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(t => t.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Lead>().WithMany().HasForeignKey(t => t.LeadId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.Property(m => m.Subject).HasMaxLength(500);
        builder.Property(m => m.FromAddress).HasMaxLength(256);
        builder.Property(m => m.FromName).HasMaxLength(256);
        builder.Property(m => m.ToAddress).HasMaxLength(256);
        builder.Property(m => m.AuthSummary).HasMaxLength(100);
        builder.Property(m => m.RawMessageId).HasMaxLength(500);
        builder.Property(m => m.ClassificationConfidence).HasPrecision(5, 4);
        builder.HasIndex(m => new { m.ThreadId, m.OccurredAt });
        // inbound dedupe: SendGrid redelivers, so the same Message-ID must never create a second row (Direction 0 = Inbound)
        builder.HasIndex(m => new { m.WorkspaceId, m.RawMessageId }).IsUnique()
            .HasFilter("[RawMessageId] IS NOT NULL AND [Direction] = 0");
        builder.HasIndex(m => m.EmailMessageId);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InboxThread>().WithMany().HasForeignKey(m => m.ThreadId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class InboundQuarantineConfiguration : IEntityTypeConfiguration<InboundQuarantine>
{
    public void Configure(EntityTypeBuilder<InboundQuarantine> builder)
    {
        builder.ToTable("InboundQuarantine");
        builder.Property(q => q.Reason).HasMaxLength(32);
        builder.Property(q => q.FromAddress).HasMaxLength(256);
        builder.Property(q => q.ToAddress).HasMaxLength(1000);
        builder.Property(q => q.Subject).HasMaxLength(500);
        builder.Property(q => q.HeadersSnippet).HasMaxLength(4096);
        builder.HasIndex(q => q.ReceivedAt);                                    // 30-day purge (F29 housekeeping)
    }
}
