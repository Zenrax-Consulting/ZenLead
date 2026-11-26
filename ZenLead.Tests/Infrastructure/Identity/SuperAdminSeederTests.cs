using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Identity;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Identity;

public class SuperAdminSeederTests
{
    // generated per run: no fixed credentials anywhere in the repo
    private static readonly string Email = $"platform-{Guid.NewGuid():N}@zenlead.test";
    private static string StrongPassword() => $"Zl!{Guid.NewGuid():N}A1";

    private static IdentityTestHost Host(string? email, string? password)
        => new(o => { o.Email = email; o.Password = password; });

    private static Task SeedAsync(IdentityTestHost host, bool isProduction = false)
        => host.RunAsync(sp => sp.GetRequiredService<SuperAdminSeeder>().SeedAsync(isProduction));

    /// <summary>Same database, different configured values — like restarting the app after editing user-secrets.</summary>
    private static Task SeedWithAsync(IdentityTestHost host, SuperAdminOptions options, bool isProduction = false)
        => host.RunAsync(sp => new SuperAdminSeeder(sp.GetRequiredService<UserManager<AppUser>>(), Options.Create(options),
            sp.GetRequiredService<ILogger<SuperAdminSeeder>>()).SeedAsync(isProduction));

    private static Task<List<AppUser>> SuperAdminsAsync(IdentityTestHost host)
        => host.RunAsync(sp => sp.GetRequiredService<UserManager<AppUser>>().Users.Where(u => u.PlatformRole == PlatformRole.SuperAdmin).ToListAsync());

    [Fact]
    public async Task Creates_the_super_admin_once_without_a_workspace()
    {
        var password = StrongPassword();
        using var host = Host(Email, password);

        await SeedAsync(host);
        await SeedAsync(host);

        var admin = Assert.Single(await SuperAdminsAsync(host));
        Assert.Equal(Email, admin.Email);
        Assert.Null(admin.WorkspaceId);
        Assert.True(admin.EmailConfirmed);
        Assert.Equal(AppUserStatus.Active, admin.Status);              // F20: users default to pending; the seeder opts in explicitly
        Assert.True(await host.RunAsync(sp => sp.GetRequiredService<UserManager<AppUser>>().CheckPasswordAsync(admin, password)));
    }

    [Fact]
    public async Task Second_run_never_resets_a_changed_password()
    {
        var original = StrongPassword();
        var changed = StrongPassword();
        using var host = Host(Email, original);
        await SeedAsync(host);

        // the owner changes the configured value later; the existing account must keep its password
        await SeedWithAsync(host, new SuperAdminOptions { Email = Email, Password = changed });

        var admin = Assert.Single(await SuperAdminsAsync(host));
        Assert.True(await host.RunAsync(sp => sp.GetRequiredService<UserManager<AppUser>>().CheckPasswordAsync(admin, original)));
        Assert.False(await host.RunAsync(sp => sp.GetRequiredService<UserManager<AppUser>>().CheckPasswordAsync(admin, changed)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("someone@zenlead.test", null)]
    public async Task Missing_config_is_skipped_in_development(string? email, string? password)
    {
        using var host = Host(email, password);

        await SeedAsync(host, isProduction: false);

        Assert.Empty(await SuperAdminsAsync(host));
        Assert.Contains("Super admin not seeded", host.Logs.All);
    }

    [Fact]
    public async Task Missing_config_throws_in_production_when_no_super_admin_exists()
    {
        using var host = Host(null, null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(host, isProduction: true));
        Assert.Contains("SuperAdmin:Email", ex.Message);
    }

    [Fact]
    public async Task Missing_config_is_fine_in_production_once_a_super_admin_exists()
    {
        using var host = Host(Email, StrongPassword());
        await SeedAsync(host, isProduction: true);

        await SeedWithAsync(host, new SuperAdminOptions(), isProduction: true);   // does not throw
        Assert.Single(await SuperAdminsAsync(host));
    }

    [Fact]
    public async Task Short_password_is_rejected()
    {
        const string password = "Sh0rt!pw";                                    // 8 chars: passes Identity, fails the seeder
        using var host = Host(Email, password);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(host));
        Assert.Contains("at least 12", ex.Message);
        Assert.DoesNotContain(password, ex.Message);
        Assert.Empty(await SuperAdminsAsync(host));
    }

    [Fact]
    public async Task Password_containing_the_email_name_is_rejected()
    {
        const string email = "rootadmin@zenlead.test";
        var password = $"X!9RootAdmin{Guid.NewGuid():N}";
        using var host = Host(email, password);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(host));

        Assert.Contains("email name", ex.Message);
        Assert.Empty(await SuperAdminsAsync(host));
    }

    [Fact]
    public async Task Password_failing_identity_rules_is_rejected()
    {
        using var host = Host(Email, "alllowercaseletters-only");   // long enough, but no digit/upper/symbol
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(host));
        Assert.Contains("Could not create the super admin", ex.Message);
        Assert.Empty(await SuperAdminsAsync(host));
    }

    [Fact]
    public async Task Email_of_an_existing_workspace_user_throws_and_is_not_promoted()
    {
        var email = $"member-{Guid.NewGuid():N}@acme.test";
        using var host = Host(email, StrongPassword());
        await host.RunAsync(async sp =>
        {
            var db = sp.GetRequiredService<ZenLeadDbContext>();
            var ws = new Workspace { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTime.UtcNow };
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();
            await sp.GetRequiredService<IIdentityService>().CreateUserAsync(ws.Id, email, StrongPassword(), "Member", AppUserStatus.Active);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(host));

        Assert.Contains("existing workspace user", ex.Message);
        Assert.Empty(await SuperAdminsAsync(host));
    }

    [Fact]
    public async Task Password_never_appears_in_log_output()
    {
        var password = StrongPassword();
        using var host = Host(Email, password);

        await SeedAsync(host);
        await SeedAsync(host);

        Assert.Contains("Super admin account created", host.Logs.All);
        Assert.DoesNotContain(password, host.Logs.All);
    }

    [Theory]
    [InlineData("abcdefghijk", "a@zenlead.test", "at least")]
    [InlineData("Xx!1-ROOTADMIN-long", "rootadmin@zenlead.test", "email name")]
    [InlineData("Xx!1-ab-long-enough", "ab@zenlead.test", null)]         // local part shorter than 3 is not checked
    [InlineData("Xx!1-perfectly-fine", "rootadmin@zenlead.test", null)]
    public void PasswordWeakness_rules(string password, string email, string? expected)
    {
        var weakness = SuperAdminSeeder.PasswordWeakness(password, email);
        if (expected is null) Assert.Null(weakness);
        else Assert.Contains(expected, weakness);
    }
}
