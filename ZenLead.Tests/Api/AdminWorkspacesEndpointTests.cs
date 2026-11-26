using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Dtos.Admin;
using ZenLead.Tests.Api.Support;

namespace ZenLead.Tests.Api;

public class AdminWorkspacesEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Url = "/api/v1/admin/workspaces";

    [Fact]
    public async Task Super_admin_creates_a_workspace_which_is_listed_and_audited()
    {
        var (adminId, token) = await factory.CreateSuperAdminAsync();
        var client = factory.ClientFor(token);
        var name = $"Zenrax {Guid.NewGuid():N}";

        var adminEmail = $"Owner{Guid.NewGuid():N}@Zenrax.test";

        var response = await client.PostAsJsonAsync(Url, new CreateWorkspaceRequest(name, adminEmail, "Asia/Dubai"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WorkspaceAdminResponse>();
        Assert.Equal(name, created!.Name);
        Assert.Equal(adminEmail.ToLowerInvariant(), created.AdminEmail);
        Assert.Equal("Asia/Dubai", created.TimeZone);
        Assert.Equal(0, created.UserCount);

        var list = await client.GetFromJsonAsync<List<WorkspaceAdminResponse>>(Url);
        Assert.Contains(list!, w => w.Id == created.Id && w.UserCount == 0 && w.TimeZone == "Asia/Dubai");

        var mail = Assert.Single(factory.Email.Sent.ToList(), m => m.To.Address == adminEmail.ToLowerInvariant());
        Assert.Contains("https://app.zenlead.test/register", mail.TextBody);        // App:PublicBaseUrl pinned by ApiFactory

        var audit = await factory.WithDbAsync(db => db.AuditLogEntries.SingleAsync(a => a.TargetId == created.Id));
        Assert.Equal("workspace.created", audit.Action);
        Assert.Equal(adminId, audit.ActorUserId);
        Assert.Contains(name, audit.DetailJson);
    }

    [Fact]
    public async Task Duplicate_name_returns_409()
    {
        var (_, token) = await factory.CreateSuperAdminAsync();
        var client = factory.ClientFor(token);
        var name = $"Dup {Guid.NewGuid():N}";
        (await client.PostAsJsonAsync(Url, new CreateWorkspaceRequest(name, "a@dup.test"))).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(Url, new CreateWorkspaceRequest(" " + name.ToUpperInvariant() + " ", "b@dup.test"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("X", "a@b.test")]
    [InlineData("Valid name", "not-an-email")]
    public async Task Invalid_request_returns_400(string name, string email)
    {
        var (_, token) = await factory.CreateSuperAdminAsync();
        var response = await factory.ClientFor(token).PostAsJsonAsync(Url, new CreateWorkspaceRequest(name, email));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_shows_user_counts_but_no_tenant_content()
    {
        var name = $"Counted {Guid.NewGuid():N}";
        var (workspaceId, _, _) = await factory.CreateWorkspaceAndUserAsync(name, $"{Guid.NewGuid():N}@counted.test");
        var (_, token) = await factory.CreateSuperAdminAsync();

        var json = await factory.ClientFor(token).GetStringAsync(Url);
        var list = System.Text.Json.JsonSerializer.Deserialize<List<WorkspaceAdminResponse>>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(1, list!.Single(w => w.Id == workspaceId).UserCount);
        Assert.DoesNotContain("leads", json, StringComparison.OrdinalIgnoreCase);
    }
}
