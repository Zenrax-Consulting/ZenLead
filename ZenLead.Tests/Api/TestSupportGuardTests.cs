using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using ZenLead.Api.Controllers.V1.Test;
using ZenLead.Tests.Api.Support;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Api;

/// <summary>
/// Security guard for the test-support endpoints (T00): they exist only in the E2E environment and only answer loopback callers.
/// The startup half (Pickup / Ai:Fake refused elsewhere, no real keys in E2E) is in <see cref="StartupConfigurationEnvironmentTests"/>.
/// </summary>
public class TestSupportGuardTests(ApiFactory development) : IClassFixture<ApiFactory>
{
    private static readonly string[] Routes =
    [
        "POST /api/v1/test/reset", "POST /api/v1/test/seed/gate2", "GET /api/v1/test/jobs", "POST /api/v1/test/jobs/run-pending",
        "POST /api/v1/test/jobs/campaign-sender/run", "GET /api/v1/test/clock", "POST /api/v1/test/clock/advance", "POST /api/v1/test/clock/set"
    ];

    public static TheoryData<string> AllRoutes() => [.. Routes];

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Development_TestRoutesDoNotExist(string route)
        => AssertNoSuchRoute((await Send(development.ClientFor(null), route)).StatusCode);

    [Fact]
    public async Task Production_TestRoutesDoNotExist()
    {
        using var production = new EnvironmentApiFactory("Production", new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "unused",
            ["Jwt:SigningKey"] = new string('k', 48),
            ["Jwt:Issuer"] = "test",
            ["Jwt:Audience"] = "test",
            ["OpenAI:ApiKey"] = "sk-test",
            ["Unsubscribe:SigningKey"] = new string('u', 48),
            ["Email:Provider"] = "SendGrid",
            ["SendGrid:ApiKey"] = "SG.test",
            ["SendGrid:InboundSecret"] = ApiFactory.InboundSecret,
            ["SendGrid:EventWebhook:PublicKey"] = EventWebhookSigner.Shared.PublicKey,
            ["LeadSource:Provider"] = "Pdl",
            ["LeadSource:Pdl:ApiKey"] = "pdl-test",
            ["App:PublicBaseUrl"] = "https://app.example.test",
            ["Hangfire:Enabled"] = "false",
            ["Seed:SuperAdmin"] = "false"
        });
        var client = production.Client();
        foreach (var route in Routes)
            AssertNoSuchRoute((await Send(client, route)).StatusCode);
    }

    /// <summary>
    /// No endpoint matched: a GET falls through to the SPA fallback (404 with no built index.html), a POST gets 405 because the
    /// fallback only answers GET. Either way the controller never ran.
    /// </summary>
    private static void AssertNoSuchRoute(HttpStatusCode status)
        => Assert.Contains(status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

    [Fact]
    public void Gate_RemovesTheControllerUnlessEnabled()
    {
        ControllerFeature Populate(bool enabled)
        {
            var feature = new ControllerFeature();
            feature.Controllers.Add(typeof(TestSupportController).GetTypeInfo());
            new TestSupportControllerGate(enabled).PopulateFeature([], feature);
            return feature;
        }

        Assert.Empty(Populate(enabled: false).Controllers);
        Assert.Single(Populate(enabled: true).Controllers);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.20")]
    [InlineData("2001:db8::1")]
    public async Task E2E_NonLoopbackCaller_Gets403(string remoteIp)
    {
        using var e2e = new E2EApiFactory();
        var client = e2e.Client(remoteIp);
        foreach (var route in Routes)
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, route)).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task E2E_LoopbackCaller_IsServed(string remoteIp)
    {
        using var e2e = new E2EApiFactory();
        var response = await e2e.Client(remoteIp).GetAsync("/api/v1/test/clock");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string route)
    {
        var (method, path) = (route.Split(' ')[0], route.Split(' ')[1]);
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = JsonContent.Create(new { minutes = 5, utc = DateTimeOffset.UtcNow });
        return client.SendAsync(request);
    }
}
