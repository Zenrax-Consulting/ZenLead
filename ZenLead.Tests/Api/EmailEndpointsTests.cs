using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Workspaces;
using ZenLead.Tests.Application.Auth;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Api;

public class OpsTestEmailTests
{
    private static OpsController Ops(FakeEmailSender sender, string? email, params string[] admins)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            admins.Select((a, i) => new KeyValuePair<string, string?>($"Hangfire:AdminEmails:{i}", a))).Build();
        var controller = new OpsController(config, new EphemeralDataProtectionProvider(), sender);
        var claims = email is null ? [] : new[] { new Claim("email", email) };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        };
        return controller;
    }

    [Fact]
    public async Task Non_ops_user_is_forbidden_and_nothing_is_sent()
    {
        var sender = new FakeEmailSender();

        var result = await Ops(sender, "user@acme.test", "boss@acme.test").TestEmail(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Ops_user_gets_the_test_email_at_their_own_address()
    {
        var sender = new FakeEmailSender { Result = SendResult.Ok("sg-1") };

        var result = await Ops(sender, "boss@acme.test", "boss@acme.test").TestEmail(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("boss@acme.test", sent.To.Address);
        Assert.Equal(["system"], sent.Categories!);
    }

    [Fact]
    public async Task Provider_failure_maps_to_502()
    {
        var sender = new FakeEmailSender { Result = SendResult.Fail("SendGrid 401: bad key", retryable: false) };

        var result = await Ops(sender, "boss@acme.test", "boss@acme.test").TestEmail(CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}

public class WorkspaceTimeZoneTests
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
    public async Task New_workspace_defaults_to_utc()
        => Assert.Equal("UTC", (await new FakeWorkspaceRepository().CreateAsync("Acme")).TimeZone);

    [Fact]
    public async Task Valid_zone_is_saved_on_the_callers_workspace()
    {
        var repo = new FakeWorkspaceRepository();
        var workspace = await repo.CreateAsync("Acme");

        var result = await Build(repo, workspace.Id).UpdateTimeZone(new UpdateTimeZoneRequest(" Asia/Dubai "), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Asia/Dubai", workspace.TimeZone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Base")]
    public async Task Unknown_zone_is_a_validation_problem(string? tz)
    {
        var repo = new FakeWorkspaceRepository();
        var workspace = await repo.CreateAsync("Acme");

        var result = await Build(repo, workspace.Id).UpdateTimeZone(new UpdateTimeZoneRequest(tz), CancellationToken.None);

        Assert.IsNotType<NoContentResult>(result);
        Assert.Equal("UTC", workspace.TimeZone);
    }

    [Fact]
    public async Task Missing_workspace_claim_is_unauthorized()
    {
        var result = await Build(new FakeWorkspaceRepository(), null).UpdateTimeZone(new UpdateTimeZoneRequest("UTC"), CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(result);
    }
}
