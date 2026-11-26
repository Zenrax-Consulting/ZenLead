namespace ZenLead.Infrastructure.Identity;

/// <summary>
/// Bound from the "SuperAdmin" config section. Set only via user-secrets locally / Key Vault in Azure —
/// there are deliberately no defaults anywhere in the repo.
/// </summary>
public class SuperAdminOptions
{
    public string? Email { get; set; }
    public string? Password { get; set; }
}
