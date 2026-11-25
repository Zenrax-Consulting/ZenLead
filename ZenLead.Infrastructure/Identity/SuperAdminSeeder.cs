using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Infrastructure.Identity;

/// <summary>
/// Creates the platform super admin from config at startup. Idempotent: an existing super admin is never changed
/// (in particular its password is never reset). Never logs the password.
/// </summary>
public class SuperAdminSeeder(UserManager<AppUser> users, IOptions<SuperAdminOptions> options, ILogger<SuperAdminSeeder> logger)
{
    public const int MinPasswordLength = 12;

    public async Task SeedAsync(bool isProduction, CancellationToken ct = default)
    {
        var o = options.Value;

        if (string.IsNullOrWhiteSpace(o.Email) || string.IsNullOrWhiteSpace(o.Password))
        {
            // only touch the database when the answer matters, so a dev machine without config still starts without SQL Server
            if (isProduction && !await users.Users.AnyAsync(u => u.PlatformRole == PlatformRole.SuperAdmin, ct))
                throw new InvalidOperationException("No super admin exists and SuperAdmin:Email / SuperAdmin:Password are not configured.");
            logger.LogWarning("Super admin not seeded: SuperAdmin:Email/Password not configured.");
            return;
        }

        var email = o.Email.Trim();
        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            if (existing.PlatformRole != PlatformRole.SuperAdmin)
                throw new InvalidOperationException("SuperAdmin:Email belongs to an existing workspace user. Choose a different address."); // never silently promote
            return;                                           // idempotent: never overwrites the password
        }

        var weakness = PasswordWeakness(o.Password, email);
        if (weakness is not null) throw new InvalidOperationException($"SuperAdmin:Password rejected: {weakness}.");

        var user = new AppUser
        {
            UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Platform admin",
            PlatformRole = PlatformRole.SuperAdmin, WorkspaceId = null
        };
        var result = await users.CreateAsync(user, o.Password);
        if (!result.Succeeded)
            throw new InvalidOperationException("Could not create the super admin: " + string.Join("; ", result.Errors.Select(e => e.Code)));
        logger.LogInformation("Super admin account created for {Email}", email);   // never the password
    }

    /// <summary>Extra rules on top of Identity's (digit/upper/lower/symbol are applied by CreateAsync). Null when acceptable.</summary>
    internal static string? PasswordWeakness(string password, string email)
        => password.Length < MinPasswordLength ? $"must be at least {MinPasswordLength} characters"
         : email.Split('@')[0] is { Length: >= 3 } local && password.Contains(local, StringComparison.OrdinalIgnoreCase) ? "must not contain the email name"
         : null;
}
