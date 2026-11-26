using System.Text;
using FluentValidation;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
using ZenLead.Application.UseCases.Leads.Import;
using Azure.Identity;
using Azure.Storage.Blobs;
using ZenLead.Infrastructure.Csv;
using ZenLead.Infrastructure.Storage;
using ZenLead.Application;
using ZenLead.Application.UseCases.Admin;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Application.UseCases.Auth;
using ZenLead.Api;
using ZenLead.Application.Validation.Auth;
using ZenLead.Infrastructure.Ai;
using ZenLead.Infrastructure.Identity;
using ZenLead.Infrastructure.Jobs;
using ZenLead.Infrastructure.LeadSources;
using ZenLead.Infrastructure.Persistence;
using SendGrid;
using ZenLead.Application.Email;
using ZenLead.Infrastructure.Email;
using ZenLead.Domain.Enums;
using ZenLead.Application.Auth;
using ZenLead.Application.UseCases.Campaigns;
using ZenLead.Infrastructure.Security;

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
        // counted by IdentityService.ValidateCredentialsAsync; protects every account, the super admin included
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
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

// Deny by default: plain [Authorize] means "signed in AND in a workspace", so the workspace-less super admin gets 403 on
// every tenant endpoint without per-controller checks. Non-tenant endpoints opt in with Policies.AnyUser.
builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim("workspace_id").Build())
    .AddPolicy(Policies.AnyUser, p => p.RequireAuthenticatedUser())
    .AddPolicy(Policies.SuperAdminOnly, p => p.RequireAuthenticatedUser().RequireClaim("role", nameof(PlatformRole.SuperAdmin)));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimiting.ComposePolicy, RateLimiting.ComposePartition);
    options.AddPolicy(RateLimiting.DiscoveryPolicy, RateLimiting.DiscoveryPartition);
    options.AddPolicy(RateLimiting.UploadPolicy, RateLimiting.UploadPartition);
    // limits are overridable (RateLimits:*) so endpoint tests can make many anonymous calls from one "IP"
    var authPermits = builder.Configuration.GetValue("RateLimits:AuthPermitLimit", RateLimiting.AuthPermitsPerMinute);
    var registerPermits = builder.Configuration.GetValue("RateLimits:RegisterPermitLimit", RateLimiting.RegisterPermitsPerWindow);
    var pickerPermits = builder.Configuration.GetValue("RateLimits:PickerPermitLimit", RateLimiting.PickerPermitsPerMinute);
    options.AddPolicy(RateLimiting.AuthPolicy, ctx => RateLimiting.AuthPartition(ctx, authPermits));
    options.AddPolicy(RateLimiting.RegisterPolicy, ctx => RateLimiting.PerIpPartition(ctx, registerPermits, RateLimiting.RegisterWindow));
    options.AddPolicy(RateLimiting.PickerPolicy, ctx => RateLimiting.PerIpPartition(ctx, pickerPermits, TimeSpan.FromMinutes(1)));
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

// super admin & workspace creation (F19)
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.Configure<SuperAdminOptions>(builder.Configuration.GetSection("SuperAdmin"));
builder.Services.AddScoped<SuperAdminSeeder>();
builder.Services.AddScoped<IAuditLog, EfAuditLog>();
builder.Services.AddScoped<CreateWorkspaceUseCase>();

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

// CSV import (F15): files live in blob storage (Azurite locally); parsing is behind ICsvRowReader
builder.Services.AddSingleton(_ =>
    builder.Configuration["Storage:AccountUri"] is { Length: > 0 } uri
        ? new BlobServiceClient(new Uri(uri), new DefaultAzureCredential())
        : new BlobServiceClient(builder.Configuration["Storage:ConnectionString"] ?? "UseDevelopmentStorage=true"));
builder.Services.AddScoped<IBlobStorage, AzureBlobStorage>();
builder.Services.AddSingleton<ICsvRowReader, CsvHelperRowReader>();
builder.Services.AddScoped<ICsvImportRepository, CsvImportRepository>();
builder.Services.AddScoped<CreateCsvImportUseCase>();
builder.Services.AddScoped<StartCsvImportUseCase>();
builder.Services.AddScoped<ProcessCsvImportUseCase>();
builder.Services.AddScoped<ProcessCsvImportJob>();

// transactional email (F18): "Log" (dev default, writes to the console) or "SendGrid"; startup rejects "Log" outside Development
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
if (string.Equals(builder.Configuration["Email:Provider"], EmailOptions.SendGridProvider, StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ISendGridClient>(_ => new SendGridClient(new SendGridClientOptions { ApiKey = builder.Configuration["SendGrid:ApiKey"] }));
    builder.Services.AddScoped<IEmailSender, SendGridEmailSender>();
}
else builder.Services.AddScoped<IEmailSender, LogEmailSender>();

// campaigns (F22): builder API and scheduling; nothing here sends mail (the sender is F23)
builder.Services.Configure<UnsubscribeOptions>(builder.Configuration.GetSection(UnsubscribeOptions.SectionName));
builder.Services.AddSingleton<IUnsubscribeTokens, HmacUnsubscribeTokens>();
builder.Services.AddScoped<ICampaignRepository, CampaignRepository>();
builder.Services.AddScoped<ICampaignEnrollmentRepository, CampaignEnrollmentRepository>();
builder.Services.AddScoped<CampaignsUseCase>();
builder.Services.AddScoped<CampaignStepsUseCase>();
builder.Services.AddScoped<EnrollLeadsUseCase>();
builder.Services.AddScoped<ActivateCampaignUseCase>();
builder.Services.AddScoped<PauseCampaignUseCase>();

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

// workspace membership & registration approval (F20)
builder.Services.Configure<RegistrationOptions>(builder.Configuration.GetSection(RegistrationOptions.SectionName));
builder.Services.AddScoped<IApprovalRepository, ApprovalRepository>();
builder.Services.AddScoped<RequestRegistrationUseCase>();
builder.Services.AddScoped<GetApprovalInfoUseCase>();
builder.Services.AddScoped<DecideApprovalUseCase>();
builder.Services.AddScoped<LoginUseCase>();
builder.Services.AddScoped<RefreshTokenUseCase>();
builder.Services.AddScoped<ComposeEmailUseCase>();

builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

var app = builder.Build();

// endpoint tests set Seed:SuperAdmin=false and seed explicitly
if (app.Configuration.GetValue("Seed:SuperAdmin", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<SuperAdminSeeder>().SeedAsync(app.Environment.IsProduction());
}

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

/// <summary>Visible to WebApplicationFactory in ZenLead.Tests.</summary>
public partial class Program { }
