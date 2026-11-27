using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Api.Support;

/// <summary>
/// Boots the real API in a chosen environment (E2E, Production, …) over in-memory SQLite, with every setting given explicitly.
/// Unlike <see cref="ApiFactory"/> nothing is swapped for a test fake: the environment's own registrations (pickup email, fake AI,
/// recorded jobs) are what run. Requests come from 127.0.0.1 unless the <see cref="RemoteIpHeader"/> header says otherwise.
/// </summary>
public class EnvironmentApiFactory(string environment, IReadOnlyDictionary<string, string?> settings, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
{
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment(environment);
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings));
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<DbContextOptions<ZenLeadDbContext>>();
            s.RemoveAll<IDbContextOptionsConfiguration<ZenLeadDbContext>>();
            s.AddDbContext<ZenLeadDbContext>(o => o.UseSqlite(_connection));
            s.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
            services?.Invoke(s);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ZenLeadDbContext>().Database.EnsureCreated();
        return host;
    }

    public HttpClient Client(string? remoteIp = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (remoteIp is not null) client.DefaultRequestHeaders.Add(RemoteIpHeader, remoteIp);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }

    /// <summary>TestServer leaves RemoteIpAddress empty; set it first in the pipeline, like Kestrel would.</summary>
    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext ctx, RequestDelegate nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = ctx.Request.Headers.TryGetValue(RemoteIpHeader, out var ip) ? IPAddress.Parse(ip.ToString()) : IPAddress.Loopback;
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }
}

/// <summary>The API in the E2E environment, configured the way the E2E runner configures it (fakes, recorded jobs, pickup mail).</summary>
public sealed class E2EApiFactory : EnvironmentApiFactory
{
    /// <param name="services">Extra registrations applied last, e.g. to make a dependency fail.</param>
    public E2EApiFactory(Action<IServiceCollection>? services = null)
        : this(Path.Combine(Path.GetTempPath(), "zenlead-e2e-tests", Guid.NewGuid().ToString("N")), services) { }

    private E2EApiFactory(string pickup, Action<IServiceCollection>? services) : base("E2E", Settings(pickup), services) => PickupDirectory = pickup;

    public string PickupDirectory { get; }
    public const string SuperAdminEmail = "platform-admin@example.test";

    private static Dictionary<string, string?> Settings(string pickup) => new()
    {
        ["ConnectionStrings:Default"] = "unused",
        ["Jwt:SigningKey"] = new string('k', 48),
        ["Jwt:Issuer"] = "test",
        ["Jwt:Audience"] = "test",
        ["Unsubscribe:SigningKey"] = new string('u', 48),
        ["SendGrid:InboundSecret"] = ApiFactory.InboundSecret,
        ["SendGrid:EventWebhook:PublicKey"] = EventWebhookSigner.Shared.PublicKey,
        ["Email:Provider"] = "Pickup",
        ["Email:PickupDirectory"] = pickup,
        ["Email:InboundDomain"] = "reply.example.test",
        ["Ai:Provider"] = "Fake",
        ["LeadSource:Provider"] = "Fake",
        ["Hangfire:Enabled"] = "false",
        ["Seed:SuperAdmin"] = "false",
        ["Sending:Mode"] = "Restricted",
        ["App:PublicBaseUrl"] = "https://app.example.test",
        ["SuperAdmin:Email"] = SuperAdminEmail,
        ["SuperAdmin:Password"] = ApiFactory.NewPassword(),
        // appsettings.E2E.json is loaded from the API's content root; make sure no real key could come from anywhere
        ["SendGrid:ApiKey"] = "",
        ["OpenAI:ApiKey"] = "",
        ["LeadSource:Pdl:ApiKey"] = ""
    };

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(PickupDirectory)) Directory.Delete(PickupDirectory, recursive: true);
    }
}
