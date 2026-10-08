namespace ZenLead.Domain.Entities;

public class TargetProfile : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CriteriaJson { get; set; } = "{}";     // serialized LeadSearchCriteria (Web defaults)
    public Guid? SourceLeadId { get; set; }              // lead it was suggested from, if any (informational)
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
