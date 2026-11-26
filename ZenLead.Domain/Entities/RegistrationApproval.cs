namespace ZenLead.Domain.Entities;

/// <summary>
/// A single-use link the workspace admin clicks to approve or reject a registration (F20).
/// Deliberately not an <see cref="ITenantEntity"/>: it is read by an anonymous endpoint where the token is the credential,
/// so the tenant filter cannot apply; code scopes it by token hash instead.
/// </summary>
public class RegistrationApproval
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string TokenHash { get; set; } = string.Empty;         // Base64 SHA-256 of the raw token; the raw token is never stored
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }                        // CreatedAt + ApprovalLifetimeDays
    public DateTime? UsedAt { get; set; }
    public ApprovalDecision? Decision { get; set; }
}

public enum ApprovalDecision { Approved = 1, Rejected = 2 }
