using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

/// <summary>
/// An address this workspace must never email again. Survives lead deletion and covers addresses with no lead
/// (spam complaints, unsubscribe replies). Checked at ingestion, enrollment and send.
/// </summary>
public class SuppressedEmail : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Email { get; set; } = string.Empty;       // lowercase
    public SuppressionReason Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? SourceLeadId { get; set; }
}
