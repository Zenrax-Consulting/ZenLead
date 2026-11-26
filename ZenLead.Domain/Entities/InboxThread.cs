using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

/// <summary>One conversation with a lead; one thread per campaign enrollment (F25). Outbound sends and inbound replies attach to it.</summary>
public class InboxThread : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid LeadId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? EnrollmentId { get; set; }                     // one thread per enrollment (unique)
    public string Subject { get; set; } = string.Empty;         // the first outbound subject; replies keep it
    public DateTime LastMessageAt { get; set; }
    public bool IsRead { get; set; }                            // false while there is an unread inbound message
    public ReplyClassification Classification { get; set; }    // denormalised: latest inbound classification (list filter/chips)
    public DateTime CreatedAt { get; set; }
}
