using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Api;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Tests.Api.Support;

namespace ZenLead.Tests.Api;

public class AuthorizationMatrixTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>Endpoints that may be called without a token. Later features add theirs (F29.3 finishes the list).</summary>
    private static readonly HashSet<string> AnonymousAllowList =
    [
        "POST api/v1/auth/register",
        "POST api/v1/auth/login",
        "POST api/v1/auth/refresh",
        // F20: registration picker and approval links (the token is the credential)
        "GET api/v1/workspaces",
        "GET api/v1/auth/approvals/{token}",
        "POST api/v1/auth/approvals/{token}/approve",
        "POST api/v1/auth/approvals/{token}/reject"
    ];

    private static readonly HashSet<string?> KnownPolicies = [null, Policies.AnyUser, Policies.SuperAdminOnly];

    private record ApiEndpoint(string Method, string Route, IReadOnlyList<IAuthorizeData> Authorize, bool AllowAnonymous)
    {
        public string Key => $"{Method} {Route}";
        public bool IsTenantOnly => !AllowAnonymous && Authorize.Count > 0 && Authorize.All(a => a.Policy is null && a.Roles is null);
        public bool IsSuperAdminOnly => Authorize.Any(a => a.Policy == Policies.SuperAdminOnly);

        /// <summary>A concrete URL: route parameters replaced by a random guid.</summary>
        public string Url => "/" + Regex.Replace(Route, @"\{[^}]+\}", Guid.NewGuid().ToString());
    }

    private IReadOnlyList<ApiEndpoint> ControllerEndpoints()
        => factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => new ApiEndpoint(
                m, e.RoutePattern.RawText!.TrimStart('/'),
                e.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                e.Metadata.GetMetadata<IAllowAnonymous>() is not null)))
            .ToList();

    [Fact]
    public void Every_endpoint_is_explicitly_anonymous_or_guarded_by_a_known_policy()
    {
        var endpoints = ControllerEndpoints();
        Assert.NotEmpty(endpoints);

        var problems = new List<string>();
        foreach (var e in endpoints)
        {
            var anonymous = e.AllowAnonymous || e.Authorize.Count == 0;
            if (anonymous)
            {
                if (!AnonymousAllowList.Contains(e.Key)) problems.Add($"{e.Key} is reachable without a token but is not in the anonymous allow-list.");
            }
            else if (e.Authorize.Any(a => !KnownPolicies.Contains(a.Policy) || a.Roles is not null))
                problems.Add($"{e.Key} uses an unknown policy or roles; use the default policy, Policies.AnyUser or Policies.SuperAdminOnly.");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        // the allow-list must not go stale either
        Assert.All(AnonymousAllowList, key => Assert.Contains(endpoints, e => e.Key == key));
    }

    [Fact]
    public void Admin_routes_are_super_admin_only_and_tenant_routes_use_the_default_policy()
    {
        var endpoints = ControllerEndpoints();
        var admin = endpoints.Where(e => e.Route.StartsWith("api/v1/admin/")).ToList();
        Assert.NotEmpty(admin);
        Assert.All(admin, e => Assert.True(e.IsSuperAdminOnly, e.Key));
        Assert.All(endpoints.Where(e => e.Route.StartsWith("api/v1/leads") || e.Route.StartsWith("api/v1/workspaces/current")),
            e => Assert.True(e.IsTenantOnly, e.Key));
    }

    [Theory]
    [InlineData("/api/v1/leads")]
    [InlineData("/api/v1/companies")]
    [InlineData("/api/v1/target-profiles")]
    [InlineData("/api/v1/ai/token-usage")]
    [InlineData("/api/v1/workspaces/current")]
    public async Task Super_admin_gets_403_on_tenant_endpoints(string url)
    {
        var (_, token) = await factory.CreateSuperAdminAsync();
        var response = await factory.ClientFor(token).GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Super_admin_gets_403_on_every_tenant_endpoint_in_the_inventory()
    {
        var (_, token) = await factory.CreateSuperAdminAsync();
        var client = factory.ClientFor(token);
        var tenantEndpoints = ControllerEndpoints().Where(e => e.IsTenantOnly).ToList();
        Assert.NotEmpty(tenantEndpoints);

        foreach (var e in tenantEndpoints)
        {
            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(e.Method), e.Url));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{e.Key} returned {(int)response.StatusCode} for the super admin");
        }
    }

    [Fact]
    public async Task Super_admin_can_use_any_user_endpoints()
    {
        var (_, token) = await factory.CreateSuperAdminAsync();
        var response = await factory.ClientFor(token).GetAsync("/api/v1/ops/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Regular_user_gets_403_on_admin_endpoints()
    {
        var (_, _, token) = await factory.CreateWorkspaceAndUserAsync($"Acme {Guid.NewGuid():N}", $"{Guid.NewGuid():N}@acme.test");
        var client = factory.ClientFor(token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/admin/workspaces", new { name = "Sneaky", adminEmail = "a@b.test" })).StatusCode);
    }

    [Fact]
    public async Task Regular_user_still_reaches_tenant_endpoints()
    {
        var (_, _, token) = await factory.CreateWorkspaceAndUserAsync($"Acme {Guid.NewGuid():N}", $"{Guid.NewGuid():N}@acme.test");
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(token).GetAsync("/api/v1/workspaces/current")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_gets_401_on_tenant_and_admin_endpoints()
    {
        var client = factory.ClientFor(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/admin/workspaces", new { name = "X", adminEmail = "a@b.test" })).StatusCode);
    }

    [Fact]
    public async Task Refreshed_super_admin_token_keeps_role_and_has_no_workspace()
    {
        var (userId, _) = await factory.CreateSuperAdminAsync();
        string refreshToken;
        using (var scope = factory.Services.CreateScope())
            refreshToken = await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().IssueAsync(userId);

        var response = await factory.ClientFor(null).PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshToken));
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(auth!.AccessToken);
        Assert.Contains(jwt.Claims, c => c.Type == "role" && c.Value == "SuperAdmin");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "workspace_id");

        // and the refreshed token is still locked out of tenant data
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(auth.AccessToken).GetAsync("/api/v1/leads")).StatusCode);
    }
}
