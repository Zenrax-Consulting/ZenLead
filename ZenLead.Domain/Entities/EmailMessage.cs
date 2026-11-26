using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

/// <summary>
/// One campaign email for one (enrollment, step). Inserted as <see cref="EmailMessageStatus.Queued"/> BEFORE the provider call;
/// the unique (EnrollmentId, StepId) index is the double-send guard (at-most-once delivery, F23).
/// </summary>
public class EmailMessage : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid StepId { get; set; }
    public Guid LeadId { get; set; }
    public string? ProviderMessageId { get; set; }          // SendGrid X-Message-Id (event correlation fallback; primary is the emailMessageId custom arg)
    public string? SmtpMessageId { get; set; }              // real RFC Message-ID, filled by F28 events if useful for threading (F25 fallback)
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;        // the text actually sent (incl. footer)
    public bool UsedAi { get; set; }
    public EmailMessageStatus Status { get; set; }
    public DateTime QueuedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? BouncedAt { get; set; }
    public string? Error { get; set; }                      // ≤ 500 chars, no recipient data
}
