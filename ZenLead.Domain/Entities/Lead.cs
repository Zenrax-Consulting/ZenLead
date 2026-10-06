using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

public class Lead
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Title { get; set; }
    public LeadStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}
