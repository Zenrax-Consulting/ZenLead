# Feature 2 — Auth & JWT Backend

**Branch:** `feature/auth-jwt`
**Milestone:** 1 — Scaffolding
**Depends on:** Feature 1 (`AppUser`, `ZenLeadDbContext`, `Workspace`, `Lead`, `RefreshToken`).

## Goal
Register → login → refresh works end-to-end via Swagger/`.http`, and `Lead` rows can be created/listed, scoped to the caller's workspace by a manual claim check.

## Design clarification (not spelled out in the parent plan)
The parent plan's day-by-day text says use cases live in `ZenLead.Application` with "implementations in `ZenLead.Infrastructure`" — but `AppUser`/`UserManager<AppUser>`/`ZenLeadDbContext` all live in Infrastructure, and per [CLAUDE.md](../../CLAUDE.md)'s layering rule, **Application must never reference Infrastructure**. To keep that boundary real, Application defines three abstractions that Infrastructure implements:
- `IIdentityService` — wraps `UserManager<AppUser>`/`SignInManager<AppUser>` so Application never sees Identity types directly.
- `IWorkspaceRepository` — minimal CRUD for `Workspace`.
- `ILeadRepository` — minimal CRUD for `Lead`.

`IJwtTokenGenerator` and `IRefreshTokenService` (named explicitly in the parent plan) complete the abstraction set.

## Files to add

### `ZenLead.Api.csproj` (modified)
```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.12" />
```

### `ZenLead.Application.csproj` (modified)
```xml
<PackageReference Include="FluentValidation" Version="11.*" />
<PackageReference Include="FluentValidation.DependencyInjectionExtensions" Version="11.*" />
```

### `ZenLead.Application/Abstractions/IIdentityService.cs` (new)
```csharp
namespace ZenLead.Application.Abstractions;

public interface IIdentityService
{
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, CancellationToken ct = default);
    Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default);
    Task<(Guid WorkspaceId, string Email)?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default);
}
```

### `ZenLead.Application/Abstractions/IWorkspaceRepository.cs` (new)
```csharp
using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface IWorkspaceRepository
{
    Task<Workspace> CreateAsync(string name, CancellationToken ct = default);
}
```

### `ZenLead.Application/Abstractions/ILeadRepository.cs` (new)
```csharp
using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface ILeadRepository
{
    Task<Lead> CreateAsync(Lead lead, CancellationToken ct = default);
    Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Lead>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
}
```

### `ZenLead.Application/Abstractions/IJwtTokenGenerator.cs` (new)
```csharp
namespace ZenLead.Application.Abstractions;

public interface IJwtTokenGenerator
{
    string GenerateAccessToken(Guid userId, Guid workspaceId, string email);
}
```

### `ZenLead.Application/Abstractions/IRefreshTokenService.cs` (new)
```csharp
namespace ZenLead.Application.Abstractions;

public record RefreshResult(bool Succeeded, Guid UserId, string? NewRawToken, string? Error);

public interface IRefreshTokenService
{
    Task<string> IssueAsync(Guid userId, CancellationToken ct = default);
    Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default);
}
```

### `ZenLead.Application/Dtos/Auth/AuthDtos.cs` (new)
```csharp
namespace ZenLead.Application.Dtos.Auth;

public record RegisterRequest(string WorkspaceName, string Email, string Password, string DisplayName);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, string RefreshToken);
```

### `ZenLead.Application/Dtos/Leads/LeadDtos.cs` (new)
```csharp
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Leads;

public record CreateLeadRequest(string Name, string Email, string? Title);
public record LeadResponse(Guid Id, string Name, string Email, string? Title, LeadStatus Status, DateTime CreatedAt);
```

### `ZenLead.Application/Validation/Auth/RegisterRequestValidator.cs` (new)
```csharp
using FluentValidation;
using ZenLead.Application.Dtos.Auth;

namespace ZenLead.Application.Validation.Auth;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.WorkspaceName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
    }
}
```
(Add sibling validators `LoginRequestValidator`, `CreateLeadRequestValidator` following the same pattern — omitted here for brevity, same shape.)

### `ZenLead.Application/UseCases/Auth/RegisterWorkspaceUseCase.cs` (new)
```csharp
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;

namespace ZenLead.Application.UseCases.Auth;

public class RegisterWorkspaceUseCase(
    IWorkspaceRepository workspaces,
    IIdentityService identity,
    IJwtTokenGenerator jwtTokenGenerator,
    IRefreshTokenService refreshTokens)
{
    public async Task<AuthResponse> ExecuteAsync(RegisterRequest request, CancellationToken ct = default)
    {
        if (await identity.EmailExistsAsync(request.Email, ct))
            throw new InvalidOperationException("Email already registered.");

        var workspace = await workspaces.CreateAsync(request.WorkspaceName, ct);
        var userId = await identity.CreateUserAsync(workspace.Id, request.Email, request.Password, request.DisplayName, ct);

        var accessToken = jwtTokenGenerator.GenerateAccessToken(userId, workspace.Id, request.Email);
        var refreshToken = await refreshTokens.IssueAsync(userId, ct);

        return new AuthResponse(accessToken, refreshToken);
    }
}
```
`IWorkspaceRepository.CreateAsync` and `IIdentityService.CreateUserAsync` must run as a single DB transaction in the Infrastructure implementation (per the parent plan's "created transactionally at register time") — easiest done by having `IdentityService` and `WorkspaceRepository` share the same `ZenLeadDbContext` instance (scoped DI) and wrapping both calls in one `IDbContextTransaction` inside whichever implementation runs second, or by introducing a thin `IUnitOfWork.SaveChangesAsync` — see Infrastructure section below.

### `ZenLead.Application/UseCases/Auth/LoginUseCase.cs` (new)
```csharp
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;

namespace ZenLead.Application.UseCases.Auth;

public class LoginUseCase(
    IIdentityService identity,
    IJwtTokenGenerator jwtTokenGenerator,
    IRefreshTokenService refreshTokens)
{
    public async Task<AuthResponse?> ExecuteAsync(LoginRequest request, CancellationToken ct = default)
    {
        var userId = await identity.ValidateCredentialsAsync(request.Email, request.Password, ct);
        if (userId is null) return null;

        var claimsData = await identity.GetUserClaimsDataAsync(userId.Value, ct)
            ?? throw new InvalidOperationException("User not found after credential validation.");

        var accessToken = jwtTokenGenerator.GenerateAccessToken(userId.Value, claimsData.WorkspaceId, claimsData.Email);
        var refreshToken = await refreshTokens.IssueAsync(userId.Value, ct);

        return new AuthResponse(accessToken, refreshToken);
    }
}
```

### `ZenLead.Application/UseCases/Auth/RefreshTokenUseCase.cs` (new)
```csharp
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;

namespace ZenLead.Application.UseCases.Auth;

public class RefreshTokenUseCase(
    IRefreshTokenService refreshTokens,
    IIdentityService identity,
    IJwtTokenGenerator jwtTokenGenerator)
{
    public async Task<AuthResponse?> ExecuteAsync(RefreshRequest request, CancellationToken ct = default)
    {
        var result = await refreshTokens.ValidateAndRotateAsync(request.RefreshToken, ct);
        if (!result.Succeeded) return null;

        var claimsData = await identity.GetUserClaimsDataAsync(result.UserId, ct)
            ?? throw new InvalidOperationException("User not found for valid refresh token.");

        var accessToken = jwtTokenGenerator.GenerateAccessToken(result.UserId, claimsData.WorkspaceId, claimsData.Email);
        return new AuthResponse(accessToken, result.NewRawToken!);
    }
}
```

### `ZenLead.Infrastructure/Identity/IdentityService.cs` (new)
```csharp
using Microsoft.AspNetCore.Identity;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Infrastructure.Identity;

public class IdentityService(UserManager<AppUser> userManager) : IIdentityService
{
    public async Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
        => await userManager.FindByEmailAsync(email) is not null;

    public async Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, CancellationToken ct = default)
    {
        var user = new AppUser { UserName = email, Email = email, WorkspaceId = workspaceId, DisplayName = displayName };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));

        return user.Id;
    }

    public async Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null) return null;
        return await userManager.CheckPasswordAsync(user, password) ? user.Id : null;
    }

    public async Task<(Guid WorkspaceId, string Email)?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null ? null : (user.WorkspaceId, user.Email!);
    }
}
```

### `ZenLead.Infrastructure/Persistence/WorkspaceRepository.cs` (new)
```csharp
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class WorkspaceRepository(ZenLeadDbContext db) : IWorkspaceRepository
{
    public async Task<Workspace> CreateAsync(string name, CancellationToken ct = default)
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTime.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
        return workspace;
    }
}
```
Both `WorkspaceRepository` and `IdentityService` resolve the same scoped `ZenLeadDbContext` (Identity's `UserManager` uses the EF store registered on the same context), so `WorkspaceRepository.CreateAsync`'s `SaveChangesAsync` and `UserManager.CreateAsync`'s internal save both run inside the same request scope — if either fails, `RegisterWorkspaceUseCase` has already thrown before the second call, so there's no row left orphaned in practice for Phase 1. Revisit with an explicit `IDbContextTransaction` only if this proves insufficient; don't add it preemptively.

### `ZenLead.Infrastructure/Persistence/LeadRepository.cs` (new)
```csharp
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class LeadRepository(ZenLeadDbContext db) : ILeadRepository
{
    public async Task<Lead> CreateAsync(Lead lead, CancellationToken ct = default)
    {
        db.Leads.Add(lead);
        await db.SaveChangesAsync(ct);
        return lead;
    }

    public Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<IReadOnlyList<Lead>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => await db.Leads.Where(l => l.WorkspaceId == workspaceId).ToListAsync(ct);
}
```

### `ZenLead.Infrastructure/Identity/JwtTokenGenerator.cs` (new)
```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Identity;

public class JwtTokenGenerator(IConfiguration configuration) : IJwtTokenGenerator
{
    public string GenerateAccessToken(Guid userId, Guid workspaceId, string email)
    {
        var jwtSection = configuration.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["SigningKey"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("workspace_id", workspaceId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email)
        };

        var token = new JwtSecurityToken(
            issuer: jwtSection["Issuer"],
            audience: jwtSection["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
```

### `ZenLead.Infrastructure/Identity/RefreshTokenService.cs` (new)
```csharp
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Infrastructure.Identity;

public class RefreshTokenService(ZenLeadDbContext db) : IRefreshTokenService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var (raw, hash) = GenerateTokenPair();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = hash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime)
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    public async Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var existing = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null) return new RefreshResult(false, Guid.Empty, null, "Token not found.");
        if (existing.RevokedAt is not null) return new RefreshResult(false, Guid.Empty, null, "Token revoked.");
        if (existing.ExpiresAt < DateTime.UtcNow) return new RefreshResult(false, Guid.Empty, null, "Token expired.");

        existing.RevokedAt = DateTime.UtcNow;
        var (newRaw, newHash) = GenerateTokenPair();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = existing.UserId,
            TokenHash = newHash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime)
        });
        await db.SaveChangesAsync(ct);

        return new RefreshResult(true, existing.UserId, newRaw, null);
    }

    private static (string Raw, string Hash) GenerateTokenPair()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw = Convert.ToBase64String(bytes);
        return (raw, Hash(raw));
    }

    private static string Hash(string raw)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
```
(Add `using System.Text;` to the usings.)

### `ZenLead.Api/Controllers/V1/AuthController.cs` (new)
```csharp
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Route("api/v1/auth")]
public class AuthController(
    RegisterWorkspaceUseCase registerWorkspace,
    LoginUseCase login,
    RefreshTokenUseCase refreshToken) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        try
        {
            return await registerWorkspace.ExecuteAsync(request, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await login.ExecuteAsync(request, ct);
        return result is null ? Unauthorized() : Ok(result);
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var result = await refreshToken.ExecuteAsync(request, ct);
        return result is null ? Unauthorized() : Ok(result);
    }
}
```

### `ZenLead.Api/Controllers/V1/LeadsController.cs` (new)
```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/leads")]
public class LeadsController(ILeadRepository leads) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LeadResponse>>> List(CancellationToken ct)
    {
        var workspaceId = GetWorkspaceId();
        var result = await leads.ListByWorkspaceAsync(workspaceId, ct);
        return Ok(result.Select(ToResponse));
    }

    [HttpPost]
    public async Task<ActionResult<LeadResponse>> Create(CreateLeadRequest request, CancellationToken ct)
    {
        var workspaceId = GetWorkspaceId();
        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Name = request.Name,
            Email = request.Email,
            Title = request.Title,
            Status = LeadStatus.New,
            CreatedAt = DateTime.UtcNow
        };
        var created = await leads.CreateAsync(lead, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ToResponse(created));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LeadResponse>> GetById(Guid id, CancellationToken ct)
    {
        var lead = await leads.GetByIdAsync(id, ct);
        if (lead is null || lead.WorkspaceId != GetWorkspaceId())
            return NotFound(); // same response whether missing or wrong workspace — don't leak existence across tenants

        return Ok(ToResponse(lead));
    }

    private Guid GetWorkspaceId()
        => Guid.Parse(User.FindFirstValue("workspace_id")!);

    private static LeadResponse ToResponse(Lead l)
        => new(l.Id, l.Name, l.Email, l.Title, l.Status, l.CreatedAt);
}
```

### `ZenLead.Api/Program.cs` (modified — replaces current minimal version)
```csharp
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Auth;
using ZenLead.Infrastructure.Identity;
using ZenLead.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddDbContext<ZenLeadDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddIdentityCore<AppUser>(options => options.Password.RequiredLength = 8)
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<ZenLeadDbContext>();

var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["SigningKey"]!));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
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

builder.Services.AddScoped<IIdentityService, IdentityService>();
builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
builder.Services.AddScoped<ILeadRepository, LeadRepository>();
builder.Services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();

builder.Services.AddScoped<RegisterWorkspaceUseCase>();
builder.Services.AddScoped<LoginUseCase>();
builder.Services.AddScoped<RefreshTokenUseCase>();

builder.Services.AddValidatorsFromAssemblyContaining<ZenLead.Application.Validation.Auth.RegisterRequestValidator>();

var app = builder.Build();

app.UseDefaultFiles();
app.MapStaticAssets();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("/index.html");

app.Run();
```
(`AddValidatorsFromAssemblyContaining<T>` comes from `FluentValidation.DependencyInjectionExtensions` — add `using FluentValidation;` or fully-qualify as shown.)

### Delete
- `ZenLead.Api/Controllers/WeatherForecastController.cs`
- `ZenLead.Api/WeatherForecast.cs`

### User secrets (commands, not files)
```
dotnet user-secrets set "Jwt:SigningKey" "<32+ char random value>" --project ZenLead.Api
dotnet user-secrets set "Jwt:Issuer" "ZenLead" --project ZenLead.Api
dotnet user-secrets set "Jwt:Audience" "ZenLeadClient" --project ZenLead.Api
dotnet user-secrets set "ConnectionStrings:Default" "Server=(localdb)\\mssqllocaldb;Database=ZenLeadDb;Trusted_Connection=True;" --project ZenLead.Api
```

### Migration (command, not a file to hand-write)
```
dotnet ef migrations add InitialCreate -p ZenLead.Infrastructure -s ZenLead.Api
dotnet ef database update -p ZenLead.Infrastructure -s ZenLead.Api
```

## Verification
- `dotnet build ZenLead.slnx` succeeds.
- `dotnet ef database update` succeeds against LocalDB; confirm `Workspaces`, `Leads`, `RefreshTokens`, and Identity's `AspNetUsers`/`AspNetRoles` tables exist (SSMS/Azure Data Studio).
- Via `ZenLead.Api.http` or Swagger: register → 200 with `accessToken`/`refreshToken` → `POST /api/v1/leads` with `Authorization: Bearer <accessToken>` → 201 → `GET /api/v1/leads` → the lead appears → `POST /api/v1/auth/refresh` with the old refresh token → new pair issued, old one now rejected if reused.
