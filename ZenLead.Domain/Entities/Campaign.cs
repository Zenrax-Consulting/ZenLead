using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

public class Campaign : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public string? FromName { get; set; }                    // null → EmailOptions.FromName
    public int DailySendCap { get; set; } = 30;              // 1..500; F23 also applies a workspace warm-up cap
    public TimeOnly WindowStart { get; set; } = new(9, 0);   // local time in the workspace/campaign zone
    public TimeOnly WindowEnd { get; set; } = new(17, 0);    // exclusive
    public SendDays SendDays { get; set; } = SendDays.Weekdays;
    public string? TimeZoneId { get; set; }                  // optional override of Workspace.TimeZone
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public List<CampaignStep> Steps { get; set; } = [];
}
