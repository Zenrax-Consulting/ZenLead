using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Dtos.Workspaces;
using ZenLead.Tests.Application.Auth;

namespace ZenLead.Tests.Api;

public class WorkspacesControllerTests
{
    private static WorkspacesController Build(FakeWorkspaceRepository repo, Guid? workspaceClaim)
    {
        var controller = new WorkspacesController(repo);
        var claims = workspaceClaim is { } id ? [new Claim("workspace_id", id.ToString())] : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        };
        return controller;
    }

    [Fact]
    public async Task Current_returns_the_callers_workspace()
    {
        var repo = new FakeWorkspaceRepository();
        var workspace = await repo.CreateAsync("Acme");

        var result = await Build(repo, workspace.Id).Current(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(new CurrentWorkspaceResponse(workspace.Id, "Acme"), ok.Value);
    }

    [Fact]
    public async Task Current_without_workspace_claim_is_unauthorized()
    {
        var result = await Build(new FakeWorkspaceRepository(), null).Current(CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(result.Result);
    }
}
