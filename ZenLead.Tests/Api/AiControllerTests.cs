using System.Security.Claims;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using ZenLead.Api;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Application.Validation.Ai;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Application.Ai;

namespace ZenLead.Tests.Api;

internal class ThrowingEmailComposer(Exception exception) : IEmailComposer
{
    public Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
        => throw exception;
}

internal class TestProblemDetailsFactory : ProblemDetailsFactory
{
    public override ProblemDetails CreateProblemDetails(HttpContext httpContext, int? statusCode = null, string? title = null, string? type = null, string? detail = null, string? instance = null)
        => new() { Status = statusCode ?? 500, Title = title, Type = type, Detail = detail, Instance = instance };

    public override ValidationProblemDetails CreateValidationProblemDetails(HttpContext httpContext, Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelStateDictionary, int? statusCode = null, string? title = null, string? type = null, string? detail = null, string? instance = null)
        => new(modelStateDictionary) { Status = statusCode ?? 400, Title = title, Type = type, Detail = detail, Instance = instance };
}

public class AiControllerTests
{
    private static (AiController Controller, FakeTokenUsageTracker Usage) Build(
        FakeLeadRepositoryForAi leads, IEmailComposer composer, Guid callerWorkspaceId)
    {
        var usage = new FakeTokenUsageTracker();
        var useCase = new ComposeEmailUseCase(leads, composer, usage, new AiPricing());
        var controller = new AiController(useCase, new ComposeEmailRequestValidator(), usage)
        {
            ProblemDetailsFactory = new TestProblemDetailsFactory()
        };
        var identity = new ClaimsIdentity([new Claim("workspace_id", callerWorkspaceId.ToString())], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return (controller, usage);
    }

    private static Lead SeedLead(FakeLeadRepositoryForAi leads, Guid workspaceId)
    {
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        leads.Seed(lead);
        return lead;
    }

    [Fact]
    public async Task ComposeEmail_LeadNotFoundInCallersWorkspace_ReturnsNotFound()
    {
        var (controller, _) = Build(new FakeLeadRepositoryForAi(), new FakeEmailComposer(), Guid.NewGuid());

        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(Guid.NewGuid(), null), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ComposeEmail_LeadInAnotherWorkspace_ReturnsNotFound()
    {
        var leads = new FakeLeadRepositoryForAi();
        var lead = SeedLead(leads, Guid.NewGuid());
        var (controller, _) = Build(leads, new FakeEmailComposer(), Guid.NewGuid());

        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(lead.Id, null), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ComposeEmail_LeadFound_ReturnsComposedEmailAndRecordsUsage()
    {
        var workspaceId = Guid.NewGuid();
        var leads = new FakeLeadRepositoryForAi();
        var lead = SeedLead(leads, workspaceId);
        var composer = new FakeEmailComposer { Response = new ComposedEmail("Subject", "Body", 150, 100, 50) };
        var (controller, usage) = Build(leads, composer, workspaceId);

        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(lead.Id, null), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("Subject", Assert.IsType<ComposeEmailApiResponse>(ok.Value).Subject);
        var entry = Assert.Single(usage.Entries);
        Assert.Equal(workspaceId, entry.WorkspaceId);
        Assert.Equal(100, entry.PromptTokens);
        Assert.True(entry.EstimatedCostUsd > 0);
    }

    [Fact]
    public async Task ComposeEmail_ContextTooLong_ReturnsValidationProblemAndDoesNotCallAi()
    {
        var workspaceId = Guid.NewGuid();
        var leads = new FakeLeadRepositoryForAi();
        var lead = SeedLead(leads, workspaceId);
        var composer = new FakeEmailComposer();
        var (controller, _) = Build(leads, composer, workspaceId);

        var tooLong = new string('x', ComposeEmailRequestValidator.MaxContextLength + 1);
        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(lead.Id, tooLong), CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        Assert.Null(composer.LastContext);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException), null, StatusCodes.Status504GatewayTimeout)]
    [InlineData(typeof(AiProviderException), AiProviderFailureKind.RateLimited, StatusCodes.Status429TooManyRequests)]
    [InlineData(typeof(AiProviderException), AiProviderFailureKind.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(typeof(AiProviderException), AiProviderFailureKind.InvalidResponse, StatusCodes.Status502BadGateway)]
    public async Task ComposeEmail_ComposerFails_MapsToExpectedStatus(Type exceptionType, AiProviderFailureKind? kind, int expectedStatus)
    {
        var workspaceId = Guid.NewGuid();
        var leads = new FakeLeadRepositoryForAi();
        var lead = SeedLead(leads, workspaceId);
        Exception exception = kind is null
            ? new OperationCanceledException("Simulated timeout")
            : new AiProviderException(kind.Value, "boom");
        var (controller, _) = Build(leads, new ThrowingEmailComposer(exception), workspaceId);

        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(lead.Id, null), CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(expectedStatus, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetTokenUsage_ReturnsOnlyCallersWorkspaceTotals()
    {
        var mine = Guid.NewGuid();
        var (controller, usage) = Build(new FakeLeadRepositoryForAi(), new FakeEmailComposer(), mine);
        await usage.RecordAsync(new AiUsageEntry(mine, "gpt-4o", 100, 50, 0.5m));
        await usage.RecordAsync(new AiUsageEntry(Guid.NewGuid(), "gpt-4o", 9000, 9000, 99m)); // another tenant

        var result = await controller.GetTokenUsage(CancellationToken.None);

        var summary = Assert.IsType<AiUsageSummary>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, summary.Calls);
        Assert.Equal(150, summary.TotalTokens);
        Assert.Equal(0.5m, summary.EstimatedCostUsd);
    }

    [Fact]
    public async Task ComposeRateLimit_EleventhCallInAMinuteIsDenied_ButOtherWorkspaceIsNot()
    {
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(RateLimiting.ComposePartition);
        HttpContext ContextFor(Guid workspaceId) => new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("workspace_id", workspaceId.ToString())], "test"))
        };
        var a = ContextFor(Guid.NewGuid());
        var b = ContextFor(Guid.NewGuid());

        for (var i = 0; i < RateLimiting.ComposePermitsPerMinute; i++)
            Assert.True(limiter.AttemptAcquire(a).IsAcquired);

        Assert.False(limiter.AttemptAcquire(a).IsAcquired);
        Assert.True(limiter.AttemptAcquire(b).IsAcquired);
    }
}
