namespace ZenLead.Domain.Entities;

public class Company : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Domain { get; set; }      // normalised: lowercase, no scheme, no "www."
    public string? Industry { get; set; }
    public string? Country { get; set; }
    public string? Size { get; set; }        // free text bucket from provider/CSV, e.g. "51-200" (used by F14 target profiles)
    public DateTime CreatedAt { get; set; }
}
