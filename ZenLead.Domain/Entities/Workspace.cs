namespace ZenLead.Domain.Entities;

public class Workspace
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";          // IANA id, e.g. "Asia/Dubai"; used by campaign send windows (F22)
    public DateTime CreatedAt { get; set; }
}
