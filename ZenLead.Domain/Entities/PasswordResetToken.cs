namespace ZenLead.Domain.Entities;

/// <summary>
/// A single-use forgot-password link (F21). Not an <see cref="ITenantEntity"/>: the flow is anonymous and the token is the credential,
/// so every query is scoped by token hash or user id. Works for every user, the workspace-less super admin included.
/// </summary>
public class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;     // Base64 SHA-256; the raw token only ever lives in the email
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }                    // CreatedAt + PasswordResetOptions.TokenLifetime
    public DateTime? UsedAt { get; set; }
    public string? RequestedIp { get; set; }                   // max 45 chars (IPv6); for abuse investigation only
}
