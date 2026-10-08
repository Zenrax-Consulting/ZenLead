namespace ZenLead.Domain.Entities;

/// <summary>Marks a table that belongs to one workspace. Drives the EF query filter and insert stamping.</summary>
public interface ITenantEntity
{
    Guid WorkspaceId { get; set; }
}
