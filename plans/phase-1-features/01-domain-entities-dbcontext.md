# Feature 1 — Domain & Persistence Foundation

**Branch:** `feature/domain-entities-dbcontext`
**Milestone:** 1 — Scaffolding (see [phase-1-poc-implementation-plan.md](../phase-1-poc-implementation-plan.md) §2)
**Depends on:** nothing — first feature, unblocks everything else.

## Goal
Introduce the Domain entities, the `LeadStatus` enum, and an EF Core `DbContext` skeleton (with `AppUser`) so later features have real types to build against. No DI wiring, no migration, no controllers — that's Feature 2.

## Current state (verified)
- `ZenLead.Domain/Entities/`, `ZenLead.Domain/Enums/` contain only `.gitkeep`.
- `ZenLead.Infrastructure/Persistence/` contains only `.gitkeep`.
- `ZenLead.Infrastructure.csproj` references `ZenLead.Application` only (no direct `ZenLead.Domain` reference, no EF Core/Identity packages).
- All projects target `net10.0`, `Nullable=enable`, `ImplicitUsings=enable`.

## Files to add

### `ZenLead.Domain/Enums/LeadStatus.cs` (new)
```csharp
namespace ZenLead.Domain.Enums;

public enum LeadStatus
{
    New,
    Contacted,
    Replied,
    Unsubscribed
}
```

### `ZenLead.Domain/Entities/Workspace.cs` (new)
```csharp
namespace ZenLead.Domain.Entities;

public class Workspace
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
```

### `ZenLead.Domain/Entities/Lead.cs` (new)
```csharp
using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

public class Lead
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Title { get; set; }
    public LeadStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

### `ZenLead.Domain/Entities/RefreshToken.cs` (new)
```csharp
namespace ZenLead.Domain.Entities;

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
```

### `ZenLead.Infrastructure.csproj` (modified)
Add a direct reference to Domain (currently only transitive via Application — Persistence code below uses Domain types directly) and the EF Core/Identity packages:
```xml
<ItemGroup>
  <ProjectReference Include="..\ZenLead.Application\ZenLead.Application.csproj" />
  <ProjectReference Include="..\ZenLead.Domain\ZenLead.Domain.csproj" />
</ItemGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.12" />
  <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12">
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    <PrivateAssets>all</PrivateAssets>
  </PackageReference>
  <PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.12" />
</ItemGroup>
```
(Run `dotnet add package` rather than hand-typing versions — confirm whatever net10.0-compatible version it resolves to.)

### `ZenLead.Infrastructure/Persistence/AppUser.cs` (new)
```csharp
using Microsoft.AspNetCore.Identity;

namespace ZenLead.Infrastructure.Persistence;

public class AppUser : IdentityUser<Guid>
{
    public Guid WorkspaceId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}
```

### `ZenLead.Infrastructure/Persistence/ZenLeadDbContext.cs` (new)
```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class ZenLeadDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    public ZenLeadDbContext(DbContextOptions<ZenLeadDbContext> options) : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder); // required — maps Identity's own tables
    }
}
```
No Fluent API configuration beyond the mandatory `base.OnModelCreating` call — column constraints, indexes, and relationships are deferred to Feature 2's migration (PBI 2.2), once the full model (controllers, DI) exists and there's something to actually migrate.

## Not in this feature
No `Program.cs` changes, no connection string, no migration, no controllers, no FluentValidation.

## Verification
- `dotnet build ZenLead.slnx` succeeds.
- `dotnet build ZenLead.Tests` succeeds (it already references Domain + Infrastructure — confirms no broken API surface).
- No runtime/DB verification yet — `ZenLeadDbContext` isn't in DI or given a connection string until Feature 2.
