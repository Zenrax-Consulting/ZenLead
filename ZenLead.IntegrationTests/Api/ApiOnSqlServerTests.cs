using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Enums;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Api.Support;
using ZenLead.Tests.Support;

namespace ZenLead.IntegrationTests.Api;

/// <summary>
/// The API end to end on the real stack (SQL Server, migrations, Hangfire storage): sign-in and refresh rotation, lead CRUD with the
/// case-insensitive duplicate rule, and cross-workspace isolation. Re-running T02's <c>[Trait("Stack","Both")]</c> contract tests
/// here is a follow-up once T02 is merged (see docs/parking-lot.md).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class ApiOnSqlServerTests(IntegrationApiFactory factory) : IClassFixture<IntegrationApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Login_ThenRefresh_RotatesTheToken_AndTheOldOneIsRejected()
    {
        var (workspaceId, _, _) = await factory.CreateWorkspaceAndUserAsync();
        var email = $"member-{Guid.NewGuid():N}@example.test";
        var password = ApiFactory.NewPassword();
        await factory.InScopeAsync(sp => sp.GetRequiredService<IIdentityService>().CreateUserAsync(workspaceId, email, password, "Member", AppUserStatus.Active));
        var client = factory.ClientFor(null);

        var login = await (await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, password))).ReadAsync<AuthResponse>();
        var refreshed = await (await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken))).ReadAsync<AuthResponse>();
        var replay = await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken));

        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var leads = await factory.ClientFor(refreshed.AccessToken).GetAsync("/api/v1/leads");
        Assert.Equal(HttpStatusCode.OK, leads.StatusCode);
    }

    [Fact]
    public async Task CreateLead_ThenTheSameEmailInAnotherCase_Is409()
    {
        var (_, _, token) = await factory.CreateWorkspaceAndUserAsync();
        var client = factory.ClientFor(token);

        var created = await client.PostJsonAsync("/api/v1/leads", new CreateLeadRequest("Ann Example", "Ann@Example.test", "CTO", "Acme", "acme.example.test"));
        var duplicate = await client.PostJsonAsync("/api/v1/leads", new CreateLeadRequest("Ann Again", "ANN@example.TEST", null));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var lead = await created.ReadAsync<LeadResponse>();
        Assert.Equal("ann@example.test", lead.Email);
        Assert.Equal("Acme", lead.Company?.Name);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task AnotherWorkspace_CannotReadListOrDeleteALead()
    {
        var (_, _, ownerToken) = await factory.CreateWorkspaceAndUserAsync();
        var (_, _, otherToken) = await factory.CreateWorkspaceAndUserAsync();
        var lead = await (await factory.ClientFor(ownerToken).PostJsonAsync("/api/v1/leads",
            new CreateLeadRequest("Private Lead", "private@example.test", null))).ReadAsync<LeadResponse>();
        var other = factory.ClientFor(otherToken);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/leads/{lead.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/leads/{lead.Id}")).StatusCode);
        var list = await (await other.GetAsync("/api/v1/leads")).ReadAsync<PagedResult<LeadResponse>>();
        Assert.Equal(0, list.Total);
        // and the owner still has it
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(ownerToken).GetAsync($"/api/v1/leads/{lead.Id}")).StatusCode);
    }

    [Fact]
    public async Task SameEmail_InTwoWorkspaces_IsAllowed()
    {
        var (_, _, a) = await factory.CreateWorkspaceAndUserAsync();
        var (_, _, b) = await factory.CreateWorkspaceAndUserAsync();

        var inA = await factory.ClientFor(a).PostJsonAsync("/api/v1/leads", new CreateLeadRequest("Shared", "shared@example.test", null));
        var inB = await factory.ClientFor(b).PostJsonAsync("/api/v1/leads", new CreateLeadRequest("Shared", "shared@example.test", null));

        Assert.Equal(HttpStatusCode.Created, inA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, inB.StatusCode);
    }

    [Fact]
    public async Task DeletedLead_DisappearsFromTheList_AndItsEmailStaysTaken()
    {
        var (_, _, token) = await factory.CreateWorkspaceAndUserAsync();
        var client = factory.ClientFor(token);
        var lead = await (await client.PostJsonAsync("/api/v1/leads", new CreateLeadRequest("Gone", "gone@example.test", null))).ReadAsync<LeadResponse>();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/leads/{lead.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/leads/{lead.Id}")).StatusCode);
        Assert.Equal(0, (await (await client.GetAsync("/api/v1/leads")).ReadAsync<PagedResult<LeadResponse>>()).Total);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostJsonAsync("/api/v1/leads", new CreateLeadRequest("Back", "gone@example.test", null))).StatusCode);
    }
}
