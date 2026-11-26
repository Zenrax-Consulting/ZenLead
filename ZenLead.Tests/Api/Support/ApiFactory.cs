using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Api.Support;

/// <summary>
/// Boots the real API (routing, auth, policies, filters) over one in-memory SQLite database. Reused by every endpoint test.
/// No SQL Server, Hangfire or super-admin seeding at startup; tests create the users they need.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    /// <summary>Every email the API sends during the test run (nothing leaves the process).</summary>
    public FakeEmailSender Email { get; } = new();

    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["ConnectionStrings:Default"] = "unused",
        ["Jwt:SigningKey"] = new string('k', 48),
        ["Jwt:Issuer"] = "test",
        ["Jwt:Audience"] = "test",
        ["OpenAI:ApiKey"] = "test",
        ["Unsubscribe:SigningKey"] = new string('u', 48),
        ["Hangfire:Enabled"] = "false",
        ["Email:Provider"] = "Log",
        ["App:PublicBaseUrl"] = "https://app.zenlead.test",
        ["Seed:SuperAdmin"] = "false",
        // never pick up a developer's real super admin from user-secrets
        ["SuperAdmin:Email"] = "",
        ["SuperAdmin:Password"] = ""
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Development");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);       // visible to code that reads config before Build()
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(Settings)); // and wins over user-secrets afterwards
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<DbContextOptions<ZenLeadDbContext>>();
            s.RemoveAll<IDbContextOptionsConfiguration<ZenLeadDbContext>>();          // drops the UseSqlServer call from Program.cs
            s.AddDbContext<ZenLeadDbContext>(o => o.UseSqlite(_connection));
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(Email);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ZenLeadDbContext>().Database.EnsureCreated();
        return host;
    }

    /// <summary>A password that satisfies Identity's and the seeder's rules, generated per run (no fixed credentials in the repo).</summary>
    public static string NewPassword() => $"Zl!{Guid.NewGuid():N}A1";

    public async Task<(Guid WorkspaceId, Guid UserId, string AccessToken)> CreateWorkspaceAndUserAsync(string name, string email, string? password = null)
    {
        using var scope = Services.CreateScope();
        var sp = scope.ServiceProvider;
        var workspace = await sp.GetRequiredService<IWorkspaceRepository>().CreateAsync(name, adminEmail: email);
        var userId = await sp.GetRequiredService<IIdentityService>().CreateUserAsync(workspace.Id, email, password ?? NewPassword(), "Test user", AppUserStatus.Active);
        var token = sp.GetRequiredService<IJwtTokenGenerator>().GenerateAccessToken(userId, workspace.Id, email);
        return (workspace.Id, userId, token);
    }

    public async Task<(Guid UserId, string AccessToken)> CreateSuperAdminAsync(string? email = null)
    {
        using var scope = Services.CreateScope();
        var sp = scope.ServiceProvider;
        email ??= $"platform-{Guid.NewGuid():N}@zenlead.test";
        var user = new AppUser
        {
            UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Platform admin",
            PlatformRole = PlatformRole.SuperAdmin, WorkspaceId = null, Status = AppUserStatus.Active
        };
        var result = await sp.GetRequiredService<UserManager<AppUser>>().CreateAsync(user, NewPassword());
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Code)));
        var token = sp.GetRequiredService<IJwtTokenGenerator>().GenerateAccessToken(user.Id, null, email, PlatformRole.SuperAdmin);
        return (user.Id, token);
    }

    public HttpClient ClientFor(string? accessToken)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (accessToken is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>Runs <paramref name="action"/> against a context with no current workspace (the platform view).</summary>
    public async Task<T> WithDbAsync<T>(Func<ZenLeadDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ZenLeadDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
