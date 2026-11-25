namespace ZenLead.Api;

/// <summary>
/// Authorization policy names. The <em>default</em> policy (plain <c>[Authorize]</c>) is tenant-only: authenticated and
/// carrying a <c>workspace_id</c> claim — so the super admin is locked out of every tenant endpoint by default.
/// </summary>
public static class Policies
{
    /// <summary>Any signed-in user, with or without a workspace (opt-in for non-tenant endpoints).</summary>
    public const string AnyUser = "AnyUser";

    /// <summary>Platform super admin only (<c>role=SuperAdmin</c> claim).</summary>
    public const string SuperAdminOnly = "SuperAdminOnly";
}
