using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Api.Support;

namespace ZenLead.Tests.Support.Builders;

/// <summary>
/// Creates an Identity user through <see cref="UserManager{TUser}"/> (so the password hash and normalised names are real). Needs a
/// service provider with Identity, such as <c>ApiFactory.Services</c>. Defaults: an Active workspace member with a generated password.
/// </summary>
public sealed class UserBuilder
{
    private Guid? _workspaceId;
    private string _email = $"user-{Guid.NewGuid():N}@example.test";
    private string _password = ApiFactory.NewPassword();
    private string _displayName = "Test user";
    private AppUserStatus _status = AppUserStatus.Active;
    private PlatformRole _role = PlatformRole.None;

    public UserBuilder InWorkspace(Guid workspaceId) { _workspaceId = workspaceId; return this; }
    public UserBuilder WithEmail(string email) { _email = email; return this; }
    public UserBuilder WithPassword(string password) { _password = password; return this; }
    public UserBuilder Named(string displayName) { _displayName = displayName; return this; }
    public UserBuilder WithStatus(AppUserStatus status) { _status = status; return this; }

    /// <summary>The platform super admin: no workspace.</summary>
    public UserBuilder AsSuperAdmin() { _role = PlatformRole.SuperAdmin; _workspaceId = null; return this; }

    public string Password => _password;

    public async Task<AppUser> CreateAsync(IServiceProvider services)
    {
        if (_role != PlatformRole.SuperAdmin && _workspaceId is null)
            throw new InvalidOperationException("A workspace user needs InWorkspace(id); use AsSuperAdmin() for the platform admin.");

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            UserName = _email, Email = _email, EmailConfirmed = true, DisplayName = _displayName,
            WorkspaceId = _workspaceId, Status = _status, PlatformRole = _role
        };
        var result = await users.CreateAsync(user, _password);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Code)));
        return user;
    }
}
