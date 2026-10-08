using System.Security.Claims;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using ZenLead.Api;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Application.Validation.Ai;
using ZenLead.Application.Validation.Leads;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Tests.Application.Ai;

namespace ZenLead.Tests.Api;

public class StartupConfigurationTests
{
    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> Valid() => new()
    {
        ["ConnectionStrings:Default"] = "Server=(localdb)\\mssqllocaldb;Database=x;",
        ["Jwt:SigningKey"] = new string('k', 32),
        ["Jwt:Issuer"] = "zenlead",
        ["Jwt:Audience"] = "zenlead-client",
        ["OpenAI:ApiKey"] = "sk-test"
    };

    [Fact]
    public void Validate_CompleteConfiguration_HasNoProblems()
        => Assert.Empty(StartupConfiguration.Validate(Config(Valid())));

    [Theory]
    [InlineData("ConnectionStrings:Default")]
    [InlineData("Jwt:SigningKey")]
    [InlineData("Jwt:Issuer")]
    [InlineData("Jwt:Audience")]
    [InlineData("OpenAI:ApiKey")]
    public void Validate_MissingKey_NamesTheKey(string key)
    {
        var values = Valid();
        values.Remove(key);

        var problems = StartupConfiguration.Validate(Config(values));

        Assert.Contains(problems, p => p.Contains(key));
    }

    [Fact]
    public void Validate_ShortSigningKey_IsRejected()
    {
        var values = Valid();
        values["Jwt:SigningKey"] = "too-short";

        Assert.Contains(StartupConfiguration.Validate(Config(values)), p => p.Contains("at least 32 bytes"));
    }

    [Fact]
    public void ThrowIfInvalid_ListsEveryProblem()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StartupConfiguration.ThrowIfInvalid(Config([])));

        Assert.Contains("Jwt:SigningKey", ex.Message);
        Assert.Contains("OpenAI:ApiKey", ex.Message);
    }
}

public class MissingWorkspaceClaimTests
{
    private static void SetUser(ControllerBase controller, params Claim[] claims)
        => controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        };

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task LeadsController_MissingOrMalformedWorkspaceClaim_Returns401(string? claimValue)
    {
        var store = new ZenLead.Tests.Application.Leads.FakeLeadIngestionStore();
        var controller = new LeadsController(new FakeLeadRepository(), new ZenLead.Application.Leads.LeadIngestionService(store), store,
            new CreateLeadRequestValidator(), new UpdateLeadRequestValidator(), new LeadQueryValidator());
        SetUser(controller, claimValue is null ? [] : [new Claim("workspace_id", claimValue)]);

        Assert.IsType<UnauthorizedResult>((await controller.List(new LeadQuery(), CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await controller.GetById(Guid.NewGuid(), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task AiController_MissingWorkspaceClaim_Returns401()
    {
        var usage = new FakeTokenUsageTracker();
        var useCase = new ComposeEmailUseCase(new FakeLeadRepositoryForAi(), new FakeEmailComposer(), usage, new AiPricing());
        var controller = new AiController(useCase, new ComposeEmailRequestValidator(), usage);
        SetUser(controller);

        Assert.IsType<UnauthorizedResult>((await controller.ComposeEmail(new ComposeEmailApiRequest(Guid.NewGuid(), null), CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await controller.GetTokenUsage(CancellationToken.None)).Result);
    }
}

public class AuthRateLimitTests
{
    [Fact]
    public void AuthPartition_TwentyFirstRequestFromSameIpIsDenied_ButAnotherIpIsNot()
    {
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(RateLimiting.AuthPartition);
        HttpContext From(string ip)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
            return context;
        }
        var attacker = From("10.0.0.1");

        for (var i = 0; i < RateLimiting.AuthPermitsPerMinute; i++)
            Assert.True(limiter.AttemptAcquire(attacker).IsAcquired);

        Assert.False(limiter.AttemptAcquire(attacker).IsAcquired);
        Assert.True(limiter.AttemptAcquire(From("10.0.0.2")).IsAcquired);
    }
}
