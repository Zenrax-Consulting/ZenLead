using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

/// <summary>One message in an <see cref="InboxThread"/>: a campaign send (outbound) or a reply (inbound). Bodies are text; render escaped.</summary>
public class InboxMessage : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ThreadId { get; set; }
    public MessageDirection Direction { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string ToAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string TextBody { get; set; } = string.Empty;        // quoted history stripped (inbound)
    public string? FullTextBody { get; set; }                   // as received, for when stripping is wrong
    public string? HtmlBody { get; set; }                       // sanitised; not rendered in Phase 2
    public DateTime OccurredAt { get; set; }                    // received / sent
    public ReplyClassification Classification { get; set; }
    public decimal? ClassificationConfidence { get; set; }
    public string? RawMessageId { get; set; }                   // inbound Message-ID (dedupe key); outbound: SmtpMessageId when known
    public Guid? EmailMessageId { get; set; }                   // outbound: the campaign EmailMessage
    public bool SenderMismatch { get; set; }
    public bool IsAutoReply { get; set; }
    public string? AuthSummary { get; set; }                    // e.g. "dkim=pass; spf=pass"
}
