using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Application.Ai;

namespace ZenLead.Tests.Api;

internal class ThrowingEmailComposer : IEmailComposer
{
    public Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
        => throw new OperationCanceledException("Simulated timeout");
}

public class AiControllerTests
{
    private static AiController BuildController(ComposeEmailUseCase useCase, Guid callerWorkspaceId)
    {
        var controller = new AiController(useCase);
        var identity = new ClaimsIdentity([new Claim("workspace_id", callerWorkspaceId.ToString())], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task ComposeEmail_LeadNotFoundInCallersWorkspace_ReturnsNotFound()
    {
        var useCase = new ComposeEmailUseCase(new FakeLeadRepositoryForAi(), new FakeEmailComposer());
        var controller = BuildController(useCase, Guid.NewGuid());

        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(Guid.NewGuid(), null), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ComposeEmail_LeadFound_ReturnsComposedEmail()
    {
        var workspaceId = Guid.NewGuid();
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var useCase = new ComposeEmailUseCase(leads, new FakeEmailComposer());
        var controller = BuildController(useCase, workspaceId);

        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(lead.Id, null), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("Subject", Assert.IsType<ComposeEmailApiResponse>(ok.Value).Subject);
    }

    [Fact]
    public async Task ComposeEmail_ComposerTimesOut_Returns504()
    {
        var workspaceId = Guid.NewGuid();
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var useCase = new ComposeEmailUseCase(leads, new ThrowingEmailComposer());
        var controller = BuildController(useCase, workspaceId);

        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(lead.Id, null), CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, statusResult.StatusCode);
    }
}
