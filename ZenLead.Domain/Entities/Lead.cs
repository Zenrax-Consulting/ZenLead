using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

public class Lead : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;   // always stored lowercase (see LeadEmail.Normalize)
    public string? Title { get; set; }
    public LeadStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }

    public LeadSource Source { get; set; } = LeadSource.Manual;
    public Guid? SourceRunId { get; set; }               // discovery run id (F14) or CSV batch id (F15)
    public EmailVerificationStatus EmailVerificationStatus { get; set; } = EmailVerificationStatus.Unverified;

    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }             // soft delete: campaign history keeps its lead rows
}
