using System.Net.Http.Headers;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.TextGeneration;
using SendGrid;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Auth;
using ZenLead.Application.UseCases.Discovery;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Ai;
using ZenLead.Infrastructure.LeadSources;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Infrastructure.Testing;
using ZenLead.Tests.Api.Support;
using ZenLead.Tests.Support;

namespace ZenLead.IntegrationTests.Support;

/// <summary>
/// The real API (T03) over a real SQL Server database (<see cref="SqlServerFixture"/>), like <c>ApiFactory</c> but with nothing
/// swapped for an in-process fake:
/// <list type="bullet">
/// <item>EF Core on SQL Server, schema from the migrations.</item>
/// <item>Hangfire <b>enabled</b> on the same database (the real <c>HangfireJobScheduler</c> enqueues), but no in-host job server and no
/// recurring sender: tests run jobs with <see cref="HangfireTestServer"/> when they want them to run.</item>
/// <item>The real SendGrid, OpenAI (Semantic Kernel) and PDL clients, pointed at <see cref="ProviderStubs"/>.</item>
/// <item>A movable clock (<see cref="Clock"/>) registered as the app's <see cref="TimeProvider"/>.</item>
/// </list>
/// Use as a class fixture: <c>IClassFixture&lt;IntegrationApiFactory&gt;</c>, and call <see cref="ResetAsync"/> at the start of each test.
/// </summary>
public class IntegrationApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string InboundSecret = ApiFactory.InboundSecret;

    public SqlServerFixture Sql { get; } = new();
    public ProviderStubs Stubs { get; } = new();

    /// <summary>Backoff delays the discovery job asked for (F14). Recorded instead of awaited, so retry tests take no wall-clock time.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<TimeSpan> DiscoveryRetryDelays { get; } = new();

    /// <summary>The app's clock: real time until a test moves it.</summary>
    public AdjustableTimeProvider Clock { get; } = new();

    /// <summary>Settings for this factory; tests may add or change entries before the host starts (first <c>Services</c>/<c>CreateClient</c>).</summary>
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Jwt:SigningKey"] = new string('k', 48),
        ["Jwt:Issuer"] = "test",
        ["Jwt:Audience"] = "test",
        ["Unsubscribe:SigningKey"] = new string('u', 48),
        ["App:PublicBaseUrl"] = "https://app.zenlead.test",
        ["Seed:SuperAdmin"] = "false",
        ["SuperAdmin:Email"] = "",
        ["SuperAdmin:Password"] = "",
        // background jobs: Hangfire on SQL Server, but the minutely sender is never registered
        ["Hangfire:Enabled"] = "true",
        ["Sending:Enabled"] = "false",
        ["Sending:Mode"] = "Restricted",
        ["Sending:RecipientAllowList:0"] = "example.test",
        // real provider clients; every key is a dummy and every base URL is a WireMock stub (see ConfigureTestServices)
        ["Email:Provider"] = "SendGrid",
        ["SendGrid:ApiKey"] = "SG.integration-test-not-a-key",
        ["SendGrid:InboundSecret"] = InboundSecret,
        ["SendGrid:EventWebhook:PublicKey"] = EventWebhookSigner.Shared.PublicKey,
        ["Email:InboundDomain"] = "reply.zenlead.test",
        ["Ai:Provider"] = "OpenAI",
        ["OpenAI:ApiKey"] = "sk-integration-test-not-a-key",
        ["OpenAI:WarmUpOnStartup"] = "false",
        ["LeadSource:Provider"] = "Pdl",
        ["LeadSource:Pdl:ApiKey"] = "pdl-integration-test-not-a-key",
        ["Storage:ConnectionString"] = AzuriteFixture.ConnectionString,
        ["Storage:AccountUri"] = "",
        ["RateLimits:AuthPermitLimit"] = "1000",
        ["RateLimits:RegisterPermitLimit"] = "1000",
        ["RateLimits:PickerPermitLimit"] = "1000",
        ["RateLimits:ForgotPermitLimit"] = "1000",
        ["RateLimits:UnsubscribePermitLimit"] = "1000",
        ["RateLimits:ReplyPermitLimit"] = "1000"
    };

    /// <summary>Extra registrations applied after the factory's own (e.g. to make a dependency fail). Set before the host starts.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    public virtual Task InitializeAsync() => Sql.InitializeAsync();

    /// <summary>Empty database (except migrations history), default stubs, real time. Call at the start of every test.</summary>
    public async Task ResetAsync()
    {
        await Sql.ResetAsync();
        Stubs.Reset();
        Clock.Reset();
        DiscoveryRetryDelays.Clear();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Settings["ConnectionStrings:Default"] = Sql.ConnectionString;
        builder.UseEnvironment("Development");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);          // visible to code that reads config before Build()
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(Settings));  // and wins over user-secrets afterwards

        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);

            // Hangfire: an explicit storage instance (never the process-wide JobStorage.Current, which parallel factories would race on),
            // and no in-host BackgroundJobServer: HangfireTestServer runs jobs when a test asks for it
            s.RemoveAll<JobStorage>();
            s.AddSingleton(Sql.CreateHangfireStorage());
            RemoveHangfireHostedServer(s);

            // SendGrid SDK → WireMock, with a short timeout so the timeout test is quick
            s.RemoveAll<ISendGridClient>();
            s.AddSingleton<ISendGridClient>(_ => new SendGridClient(new HttpClient { Timeout = SendGridStub.ClientTimeout },
                new SendGridClientOptions { ApiKey = Settings["SendGrid:ApiKey"], Host = Stubs.SendGrid.Url }));

            // Semantic Kernel OpenAI connector → WireMock (same two services as Program.cs: default compose model + classifier)
            RemoveChatServices(s);
            var openAiHttp = new HttpClient { Timeout = OpenAiStub.ClientTimeout };
            s.AddKernel()
                .AddOpenAIChatCompletion(modelId: "gpt-4o", endpoint: Stubs.OpenAi.Endpoint, apiKey: Settings["OpenAI:ApiKey"], httpClient: openAiHttp)
                .AddOpenAIChatCompletion(modelId: "gpt-4o-mini", endpoint: Stubs.OpenAi.Endpoint, apiKey: Settings["OpenAI:ApiKey"],
                    serviceId: ClassificationModelOptions.DefaultServiceId, httpClient: openAiHttp);

            // PDL → WireMock (configure actions run in order, so this base address wins)
            s.AddHttpClient<PdlLeadSource>(c => c.BaseAddress = new Uri(Stubs.Pdl.Url + "/"));

            // discovery retries back off 2 s / 8 s / 30 s: record the delays instead of waiting them out
            s.AddScoped(sp =>
            {
                var useCase = ActivatorUtilities.CreateInstance<ProcessDiscoveryRunUseCase>(sp);
                useCase.Delay = (delay, _) => { DiscoveryRetryDelays.Enqueue(delay); return Task.CompletedTask; };
                return useCase;
            });

            ConfigureServices?.Invoke(s);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        HangfireLogging.Silence();      // startup pointed Hangfire's global logger at this host's LoggerFactory (see HangfireLogging)
        return host;
    }

    private static void RemoveHangfireHostedServer(IServiceCollection services)
    {
        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)
                                         && (d.ImplementationType?.Name ?? d.ImplementationFactory?.Method.ReturnType.Name ?? "")
                                         .Contains("BackgroundJobServer", StringComparison.Ordinal)).ToList();
        if (hosted.Count == 0)
            throw new InvalidOperationException("Could not find Hangfire's hosted BackgroundJobServer to remove; the Hangfire registration has changed.");
        foreach (var d in hosted) services.Remove(d);
    }

    private static void RemoveChatServices(IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(IChatCompletionService) || d.ServiceType == typeof(ITextGenerationService)).ToList())
            services.Remove(d);
    }

    public HttpClient ClientFor(string? accessToken)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (accessToken is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public async Task<(Guid WorkspaceId, Guid UserId, string AccessToken)> CreateWorkspaceAndUserAsync(string? name = null, string? email = null)
    {
        using var scope = Services.CreateScope();
        var sp = scope.ServiceProvider;
        email ??= $"admin-{Guid.NewGuid():N}@example.test";
        var workspace = await sp.GetRequiredService<IWorkspaceRepository>().CreateAsync(name ?? $"IT {Guid.NewGuid():N}"[..20], adminEmail: email);
        var userId = await sp.GetRequiredService<IIdentityService>().CreateUserAsync(workspace.Id, email, ApiFactory.NewPassword(), "Test user", AppUserStatus.Active);
        var token = sp.GetRequiredService<IJwtTokenGenerator>().GenerateAccessToken(userId, workspace.Id, email);
        return (workspace.Id, userId, token);
    }

    /// <summary>Runs <paramref name="action"/> in a fresh DI scope (a context with no current workspace, like a job).</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public async Task InScopeAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider);
    }

    public async Task<T> WithDbAsync<T>(Func<ZenLeadDbContext, Task<T>> action) => await InScopeAsync(sp => action(sp.GetRequiredService<ZenLeadDbContext>()));

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        Stubs.Dispose();
        await Sql.DisposeAsync();
    }
}
