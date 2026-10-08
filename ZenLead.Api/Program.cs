using System.Text;
using FluentValidation;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Microsoft.SemanticKernel;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Discovery;
using ZenLead.Application.Leads;
using ZenLead.Application.UseCases.Discovery;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Application.UseCases.Auth;
using ZenLead.Api;
using ZenLead.Application.Validation.Auth;
using ZenLead.Infrastructure.Ai;
using ZenLead.Infrastructure.Identity;
using ZenLead.Infrastructure.Jobs;
using ZenLead.Infrastructure.LeadSources;
using ZenLead.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

StartupConfiguration.ThrowIfInvalid(builder.Configuration, builder.Environment.IsDevelopment()); // fail fast with a clear message, not a NullReferenceException

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    // lets the API reference UI offer an "Authorize" box for the JWT access token
    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    };
    return Task.CompletedTask;
}));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentWorkspace, HttpCurrentWorkspace>();   // scoped like the DbContext that reads it
builder.Services.AddDbContext<ZenLeadDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddIdentityCore<AppUser>(options =>
    {
        // keep in sync with RegisterRequestValidator
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<ZenLeadDbContext>();

var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["SigningKey"]!));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep "sub" / "email" / "workspace_id" claim names as issued
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimiting.ComposePolicy, RateLimiting.ComposePartition);
    options.AddPolicy(RateLimiting.DiscoveryPolicy, RateLimiting.DiscoveryPartition);
    options.AddPolicy(RateLimiting.AuthPolicy, RateLimiting.AuthPartition);
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
});

builder.Services.AddScoped<IIdentityService, IdentityService>();
builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
builder.Services.AddScoped<ILeadRepository, LeadRepository>();
builder.Services.AddScoped<ICompanyRepository, CompanyRepository>();
builder.Services.AddScoped<ILeadIngestionStore, LeadIngestionStore>();
builder.Services.AddScoped<LeadIngestionService>();
builder.Services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();

// keep connections to OpenAI pooled longer than the default so a quiet minute does not bring back the cold-connection cost
var openAiHttpClient = new HttpClient(new SocketsHttpHandler
{
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
    PooledConnectionLifetime = TimeSpan.FromMinutes(30)
}) { Timeout = TimeSpan.FromSeconds(10) };

builder.Services.AddKernel()
    .AddOpenAIChatCompletion(modelId: builder.Configuration["OpenAI:Model"] ?? "gpt-4o", apiKey: builder.Configuration["OpenAI:ApiKey"]!, httpClient: openAiHttpClient);

builder.Services.AddScoped<IEmailComposer, EmailComposer>();
if (builder.Configuration.GetValue("OpenAI:WarmUpOnStartup", false))
    builder.Services.AddHostedService<OpenAiWarmUpService>();
builder.Services.AddScoped<ITokenUsageTracker, EfTokenUsageTracker>();
builder.Services.AddSingleton(builder.Configuration.GetSection("OpenAI").Get<AiPricing>() ?? new AiPricing());

// lead discovery (F14): the provider is chosen by LeadSource:Provider; everything vendor-specific lives in one Infrastructure class
var leadSourceOptions = builder.Configuration.GetSection("LeadSource").Get<LeadSourceOptions>() ?? new LeadSourceOptions();
builder.Services.AddSingleton(leadSourceOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FakeLeadSource>();
builder.Services.AddHttpClient<PdlLeadSource>(client =>
{
    client.BaseAddress = new Uri("https://api.peopledatalabs.com/");
    client.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["LeadSource:Pdl:ApiKey"] ?? "");
    client.Timeout = TimeSpan.FromSeconds(30);
});
var leadSourceRegistry = LeadSourceRegistry.Default();
builder.Services.AddSingleton(leadSourceRegistry);
builder.Services.AddScoped<ILeadSource>(sp => leadSourceRegistry.Resolve(leadSourceOptions.Provider, sp));
builder.Services.AddScoped<ITargetProfileRepository, TargetProfileRepository>();
builder.Services.AddScoped<IDiscoveryRunRepository, DiscoveryRunRepository>();
builder.Services.AddScoped<StartDiscoveryRunUseCase>();
builder.Services.AddScoped<ProcessDiscoveryRunUseCase>();
builder.Services.AddScoped<ProcessLeadDiscoveryJob>();

// background jobs; the switch lets endpoint tests boot without SQL Server
var hangfireEnabled = builder.Configuration.GetValue("Hangfire:Enabled", true);
if (hangfireEnabled)
{
    builder.Services.AddZenLeadHangfire(builder.Configuration);
    builder.Services.AddScoped<IJobScheduler, HangfireJobScheduler>();
}
else
{
    builder.Services.AddScoped<IJobScheduler, NoopJobScheduler>();
}

builder.Services.AddScoped<RegisterWorkspaceUseCase>();
builder.Services.AddScoped<LoginUseCase>();
builder.Services.AddScoped<RefreshTokenUseCase>();
builder.Services.AddScoped<ComposeEmailUseCase>();

builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

var app = builder.Build();

app.UseDefaultFiles();
app.MapStaticAssets();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(); // interactive API reference at /scalar
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
if (hangfireEnabled)
    app.MapHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [],
        AsyncAuthorization = [new HangfireDashboardAuthFilter(app.Services.GetRequiredService<IDataProtectionProvider>())],
        IsReadOnlyFunc = _ => false
    });
app.UseRateLimiter();

app.MapControllers();

app.MapFallbackToFile("/index.html");

app.Run();
