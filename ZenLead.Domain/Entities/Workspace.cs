namespace ZenLead.Domain.Entities;

public class Workspace
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";          // IANA id, e.g. "Asia/Dubai"; used by campaign send windows (F22)

    /// <summary>Set by the super admin when the workspace is created (F19). Nullable only so Phase 1 rows migrate; the create API requires it.</summary>
    public string? AdminEmail { get; set; }

    public DateTime CreatedAt { get; set; }
}
