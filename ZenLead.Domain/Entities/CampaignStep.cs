namespace ZenLead.Domain.Entities;

public class CampaignStep : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CampaignId { get; set; }
    public int Order { get; set; }                           // 1-based, contiguous
    public int DelayDays { get; set; }                       // calendar days after the PREVIOUS step was sent; step 1 must be 0
    public string SubjectTemplate { get; set; } = string.Empty;
    public string BodyTemplate { get; set; } = string.Empty;
    public bool UseAiPersonalisation { get; set; }
}
