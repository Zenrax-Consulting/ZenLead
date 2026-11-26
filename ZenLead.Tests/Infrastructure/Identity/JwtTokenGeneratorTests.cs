using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Identity;

namespace ZenLead.Tests.Infrastructure.Identity;

public class JwtTokenGeneratorTests
{
    private static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key-at-least-32-chars-long!!",
            ["Jwt:Issuer"] = "ZenLead",
            ["Jwt:Audience"] = "ZenLeadClient"
        })
        .Build();

    [Fact]
    public void GenerateAccessToken_IncludesExpectedClaims()
    {
        var sut = new JwtTokenGenerator(BuildConfig());
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var token = sut.GenerateAccessToken(userId, workspaceId, "a@acme.com");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(userId.ToString(), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(workspaceId.ToString(), jwt.Claims.First(c => c.Type == "workspace_id").Value);
        Assert.Equal("a@acme.com", jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.Equal("ZenLead", jwt.Issuer);
    }

    [Fact]
    public void GenerateAccessToken_RegularUser_HasNoRoleClaim()
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(new JwtTokenGenerator(BuildConfig()).GenerateAccessToken(Guid.NewGuid(), Guid.NewGuid(), "a@acme.com"));
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "role");
    }

    [Fact]
    public void GenerateAccessToken_SuperAdmin_HasRoleAndNoWorkspaceClaim()
    {
        var token = new JwtTokenGenerator(BuildConfig()).GenerateAccessToken(Guid.NewGuid(), null, "root@zenlead.test", PlatformRole.SuperAdmin);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal("SuperAdmin", jwt.Claims.Single(c => c.Type == "role").Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "workspace_id");
    }
}
