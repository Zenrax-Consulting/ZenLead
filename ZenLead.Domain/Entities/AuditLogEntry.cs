namespace ZenLead.Domain.Entities;

/// <summary>Platform-level audit trail (not an <see cref="ITenantEntity"/>). Never holds secrets.</summary>
public class AuditLogEntry
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;       // e.g. "workspace.created"
    public string? TargetType { get; set; }                  // e.g. "Workspace"
    public Guid? TargetId { get; set; }
    public string? DetailJson { get; set; }                  // only what the action needs (workspace name, admin email) — never secrets
}
