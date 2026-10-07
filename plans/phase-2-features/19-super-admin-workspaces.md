# Feature 19 — Super Admin & Workspace Creation

**Branch:** `feature/super-admin-workspaces`
**Sprint:** 2 — no email dependency; start on day one in parallel with F18.
**Depends on:** F11 (query filter; null workspace ⇒ no tenant data). F18 only for the optional "your workspace is ready" email and `Workspace.TimeZone`.

## Goal
A platform-level **super admin** whose only power is creating and listing workspaces. It belongs to no workspace, has no `workspace_id` claim, and is locked out of every tenant endpoint **by default** (not by remembering to check). Nothing else creates a workspace: the super admin makes "Zenrax" through the API/UI, which is the first step of the Gate 2 UAT script.

## Design decisions
- **Deny by default.** The ASP.NET *default authorization policy* becomes "authenticated **and** has a `workspace_id` claim". Every existing and future `[Authorize]` controller is therefore tenant-only; the super admin gets **403** on all of them without any per-controller change. Endpoints meant for any signed-in user opt in with `[Authorize(Policy = Policies.AnyUser)]`; super-admin endpoints use `Policies.SuperAdminOnly`.
- **Role as a claim, policy as the gate.** The JWT carries `role=SuperAdmin` (only for that user). Authorisation uses policies (`RequireClaim("role","SuperAdmin")`), not `[Authorize(Roles=…)]`, because `MapInboundClaims=false` is set and role-claim mapping would need extra wiring.
- **Seeded from config, never from code.** No default credentials anywhere in the repo, migrations or `appsettings*.json`.
- **Audit log** is a small new table (`AuditLogEntry`) — added to the parent plan's data-model delta by this feature.

## Files to add/modify

### Domain

**`ZenLead.Domain/Enums/PlatformRole.cs`** (new)
```csharp
namespace ZenLead.Domain.Enums;
public enum PlatformRole { None = 0, SuperAdmin = 1 }
```

**`ZenLead.Domain/Entities/Workspace.cs`** (modified) — add `public string? AdminEmail { get; set; }` (F20 also needs it; it is added here because `POST /admin/workspaces` sets it). Nullable only so Phase 1 rows migrate; the create API requires it.

**`ZenLead.Domain/Entities/AuditLogEntry.cs`** (new) — *not* an `ITenantEntity` (platform-level):
```csharp
public class AuditLogEntry
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;       // "workspace.created"
    public string? TargetType { get; set; }                  // "Workspace"
    public Guid? TargetId { get; set; }
    public string? DetailJson { get; set; }                  // never secrets/PII beyond what the action needs (workspace name, admin email)
}
```

### Infrastructure

**`ZenLead.Infrastructure/Persistence/AppUser.cs`** (modified)
```csharp
public class AppUser : IdentityUser<Guid>
{
    public Guid? WorkspaceId { get; set; }                   // null only for the super admin
    public string DisplayName { get; set; } = string.Empty;
    public PlatformRole PlatformRole { get; set; } = PlatformRole.None;
}
```
`AppUserConfiguration`: keep the optional FK to `Workspace` (`OnDelete(Restrict)`); add a **check constraint** `CK_AspNetUsers_WorkspaceOrSuperAdmin`: `([WorkspaceId] IS NOT NULL OR [PlatformRole] = 1)` — a normal user can never exist without a workspace, even by a buggy code path. Add `AuditLogEntryConfiguration` (`Action` 100, `TargetType` 50, index `(Timestamp)`), `DbSet<AuditLogEntry>`. `WorkspaceConfiguration`: `AdminEmail` 256; **unique index on `Name`** (case-insensitivity comes from SQL Server's default CI collation; the app also normalises when checking).
Migration **`AddSuperAdminAndWorkspaceAdmin`** — hand-add before the unique `Name` index:
```csharp
migrationBuilder.Sql("""
    IF EXISTS (SELECT 1 FROM Workspaces GROUP BY Name HAVING COUNT(*) > 1)
        THROW 50002, 'Duplicate workspace names exist. Rename them before applying AddSuperAdminAndWorkspaceAdmin.', 1;
    """);
```
(No production data exists; dev databases can simply be reset — the check just prevents a confusing index error.)

**`ZenLead.Infrastructure/Identity/SuperAdminOptions.cs`** + **`SuperAdminSeeder.cs`** (new)
```csharp
public class SuperAdminOptions { public string? Email { get; set; } public string? Password { get; set; } }

public class SuperAdminSeeder(UserManager<AppUser> users, IOptions<SuperAdminOptions> options, ILogger<SuperAdminSeeder> logger)
{
    public const int MinPasswordLength = 12;

    public async Task SeedAsync(bool isProduction, CancellationToken ct = default)
    {
        var o = options.Value;
        var anyExists = await users.Users.AnyAsync(u => u.PlatformRole == PlatformRole.SuperAdmin, ct);

        if (string.IsNullOrWhiteSpace(o.Email) || string.IsNullOrWhiteSpace(o.Password))
        {
            if (isProduction && !anyExists)
                throw new InvalidOperationException("No super admin exists and SuperAdmin:Email / SuperAdmin:Password are not configured.");
            logger.LogWarning("Super admin not seeded: SuperAdmin:Email/Password not configured.");
            return;
        }

        var existing = await users.FindByEmailAsync(o.Email);
        if (existing is not null)
        {
            if (existing.PlatformRole != PlatformRole.SuperAdmin)
                throw new InvalidOperationException("SuperAdmin:Email belongs to an existing workspace user. Choose a different address."); // never silently promote
            return;                                           // idempotent: never overwrites the password
        }

        var weakness = PasswordWeakness(o.Password, o.Email);
        if (weakness is not null) throw new InvalidOperationException($"SuperAdmin:Password rejected: {weakness}");

        var user = new AppUser
        {
            UserName = o.Email, Email = o.Email, EmailConfirmed = true, DisplayName = "Platform admin",
            PlatformRole = PlatformRole.SuperAdmin, WorkspaceId = null
        };
        var result = await users.CreateAsync(user, o.Password);
        if (!result.Succeeded) throw new InvalidOperationException("Could not create the super admin: " + string.Join("; ", result.Errors.Select(e => e.Code)));
        logger.LogInformation("Super admin account created for {Email}", o.Email);            // never the password
    }

    internal static string? PasswordWeakness(string password, string email)
        => password.Length < MinPasswordLength ? $"must be at least {MinPasswordLength} characters"
         : email.Split('@')[0] is { Length: >= 3 } local && password.Contains(local, StringComparison.OrdinalIgnoreCase) ? "must not contain the email name"
         : null;                                              // Identity's own rules (digit/upper/lower/symbol) are applied by CreateAsync
}
```
`Program.cs` (after `Build()`, before `Run()`):
```csharp
builder.Services.Configure<SuperAdminOptions>(builder.Configuration.GetSection("SuperAdmin"));
builder.Services.AddScoped<SuperAdminSeeder>();
// …
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<SuperAdminSeeder>().SeedAsync(app.Environment.IsProduction());
```
Skipped when `Seed:SuperAdmin=false` is set (the endpoint-test factory sets it and seeds explicitly). Local setup: `dotnet user-secrets set "SuperAdmin:Email" … ; dotnet user-secrets set "SuperAdmin:Password" … --project ZenLead.Api` — **chosen by the owner, shared with no one**; add the two keys (without values) to `README.md`.

**`ZenLead.Infrastructure/Identity/IdentityService.cs`** (modified) — `GetUserClaimsDataAsync` returns a record:
```csharp
public record UserClaimsData(Guid? WorkspaceId, string Email, PlatformRole Role);
// IIdentityService: Task<UserClaimsData?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default);
```
`CreateUserAsync(Guid workspaceId, …)` keeps its non-null signature (only workspace users are created through it). Update `FakeIdentityService` in `FakeAuthAbstractions.cs`.

**`IJwtTokenGenerator` / `JwtTokenGenerator`** (modified)
```csharp
string GenerateAccessToken(Guid userId, Guid? workspaceId, string email, PlatformRole role = PlatformRole.None);

var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId.ToString()), new(JwtRegisteredClaimNames.Email, email) };
if (workspaceId is { } w) claims.Add(new Claim("workspace_id", w.ToString()));   // super admin: no workspace_id at all
if (role != PlatformRole.None) claims.Add(new Claim("role", role.ToString()));
```
`LoginUseCase`, `RefreshTokenUseCase` and `RegisterWorkspaceUseCase` pass the role/nullable workspace through. **Look at `RefreshTokenUseCase`** (not shown in Phase 1 notes): it re-reads claims data from `IIdentityService` — make sure it also uses the new record, so a refreshed super-admin token keeps `role` and stays workspace-less. Update `FakeJwtTokenGenerator`.

### Application

**`ZenLead.Application/Dtos/Admin/AdminDtos.cs`** (new)
```csharp
public record CreateWorkspaceRequest(string Name, string AdminEmail, string? TimeZone = null);
public record WorkspaceAdminResponse(Guid Id, string Name, string? AdminEmail, string TimeZone, int UserCount, DateTime CreatedAt);
```
**`Validation/Admin/CreateWorkspaceRequestValidator.cs`** — name 2–200 trimmed; `AdminEmail` valid via `LeadEmail.Normalize`-style check + ≤ 256; optional `TimeZone` passes `TimeZones.TryFind`.

**`IWorkspaceRepository`** (modified) — `Task<Workspace> CreateAsync(string name, string? adminEmail = null, string timeZone = "UTC", CancellationToken ct = default)` (existing one-arg callers keep compiling until F20 removes workspace auto-creation from register), `Task<bool> NameExistsAsync(string name, CancellationToken ct)`, `Task<IReadOnlyList<WorkspaceAdminResponse>> ListWithUserCountsAsync(CancellationToken ct)` (reads only `Workspaces` + a `GROUP BY` count over `AspNetUsers` — **no tenant tables**), `Task<IReadOnlyList<WorkspaceName>> SearchByNameAsync(…)` arrives in F20.

**`IAuditLog`** (new interface) `Task WriteAsync(string action, Guid? actor, string? targetType, Guid? targetId, object? detail, CancellationToken ct)`; `EfAuditLog` in Infrastructure.

**`UseCases/Admin/CreateWorkspaceUseCase.cs`** (new)
```csharp
public class CreateWorkspaceUseCase(IWorkspaceRepository workspaces, IAuditLog audit, IEmailSender email, IOptions<AppOptions> app, IOptions<EmailOptions> emailOptions)
{
    public async Task<CreateWorkspaceResult> ExecuteAsync(Guid actorUserId, CreateWorkspaceRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (await workspaces.NameExistsAsync(name, ct)) return CreateWorkspaceResult.DuplicateName;

        var adminEmail = LeadEmail.Normalize(request.AdminEmail)!;                      // validator already guaranteed it
        var workspace = await workspaces.CreateAsync(name, adminEmail, request.TimeZone ?? "UTC", ct);
        await audit.WriteAsync("workspace.created", actorUserId, "Workspace", workspace.Id, new { workspace.Name, AdminEmail = adminEmail }, ct);

        // best effort: a mail failure must not undo the workspace
        await email.SendAsync(SystemEmail.Create(adminEmail, null, $"Your ZenLead workspace “{name}” is ready",
            $"A workspace named “{name}” has been created and you are its administrator. Register with this email address to get started.",
            $"{app.Value.PublicBaseUrl.TrimEnd('/')}/register", "Register"), ct);
        return CreateWorkspaceResult.Created(workspace);
    }
}
```
Race on duplicate name (two simultaneous creates): the unique index throws `DbUpdateException`; the repository converts it into `DuplicateName` too. **`AppOptions`** (`PublicBaseUrl`) — new POCO bound from `App`; `appsettings.Development.json` `"App": { "PublicBaseUrl": "https://localhost:52618" }`; used by F20/F21 as well, so `StartupConfiguration` requires it outside Development.

### Api

**`ZenLead.Api/Policies.cs`** (new) and **`Program.cs`** (modified)
```csharp
public static class Policies
{
    public const string AnyUser = "AnyUser";
    public const string SuperAdminOnly = "SuperAdminOnly";
}

builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim("workspace_id").Build())
    .AddPolicy(Policies.AnyUser, p => p.RequireAuthenticatedUser())
    .AddPolicy(Policies.SuperAdminOnly, p => p.RequireAuthenticatedUser().RequireClaim("role", nameof(PlatformRole.SuperAdmin)));
```
Mark non-tenant authenticated endpoints with `[Authorize(Policy = Policies.AnyUser)]`: `OpsController` (F14/F18). (`WorkspacesController.Current` stays tenant-only — the default `[Authorize]`.) Anything `[AllowAnonymous]` is unaffected. A 403 (not 401) is returned automatically for an authenticated caller who fails the default policy.

**`ZenLead.Api/Controllers/V1/Admin/AdminWorkspacesController.cs`** (new)
```csharp
[ApiController]
[Authorize(Policy = Policies.SuperAdminOnly)]
[Route("api/v1/admin/workspaces")]
public class AdminWorkspacesController(CreateWorkspaceUseCase create, IWorkspaceRepository workspaces, IValidator<CreateWorkspaceRequest> validator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkspaceAdminResponse>>> List(CancellationToken ct)
        => Ok(await workspaces.ListWithUserCountsAsync(ct));

    [HttpPost]
    public async Task<ActionResult<WorkspaceAdminResponse>> Create(CreateWorkspaceRequest request, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());
        var result = await create.ExecuteAsync(this.UserId()!.Value, request, ct);
        return result.IsDuplicate
            ? Conflict(new { message = "A workspace with that name already exists." })
            : CreatedAtAction(nameof(List), new WorkspaceAdminResponse(result.Workspace!.Id, result.Workspace.Name, result.Workspace.AdminEmail, result.Workspace.TimeZone, 0, result.Workspace.CreatedAt));
    }
}
```
`/api/v1/admin/**` routes need no proxy change. Login rate limit/lockout already apply to the super admin (same `AuthController`); confirm `options.Lockout` is enabled for it: `UserManager.CheckPasswordAsync` does **not** count failures — switch `IdentityService.ValidateCredentialsAsync` to `userManager.CheckPasswordAsync` + `AccessFailedAsync`/`IsLockedOutAsync`/`ResetAccessFailedCountAsync` (5 failures → 15-minute lockout, generic failure message unchanged). This is the lockout the parent plan's F29.2 lists; doing it here protects the high-value account from day one, and F29 only reviews it.

### Angular

**`core/auth/jwt-claims.ts`** (new)
```ts
export interface Claims { sub: string; email?: string; workspace_id?: string; role?: string; exp: number; }

/** UI hints only (route choice, hiding menus). The server enforces everything; never treat this as a security boundary. */
export function decodeClaims(token: string | null): Claims | null {
  if (!token) return null;
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(decodeURIComponent(atob(payload).split('').map(c => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2)).join('')));
  } catch { return null; }
}
```
**`AuthService`** (modified) — add `claims()`, `isSuperAdmin()` (`role === 'SuperAdmin'`), `homeRoute()` (`isSuperAdmin() ? '/admin/workspaces' : '/leads'`). `login`/`register`/`refresh` unchanged. **`Login`** navigates to `auth.homeRoute()` after success instead of `/leads`; `tryRestoreSession` consumers likewise.

**`core/auth/auth.guard.ts`** (modified) — keep `authGuard` (authenticated), add:
```ts
export const tenantGuard: CanActivateFn = () => {
  const auth = inject(AuthService), router = inject(Router);
  if (!auth.isAuthenticated()) return router.parseUrl('/login');
  return auth.isSuperAdmin() ? router.parseUrl('/admin/workspaces') : true;
};
export const superAdminGuard: CanActivateFn = () => {
  const auth = inject(AuthService), router = inject(Router);
  if (!auth.isAuthenticated()) return router.parseUrl('/login');
  return auth.isSuperAdmin() ? true : router.parseUrl('/leads');
};
```
(Returning `UrlTree` is the current idiom; the Phase 1 guard's imperative `navigate` can stay for `authGuard` or be aligned in passing.)

**`app-routing-module.ts`** (modified) — the shell's tenant children use `canActivate: [tenantGuard]`; add
```ts
{ path: 'admin', component: Shell, canActivate: [superAdminGuard],
  children: [{ path: 'workspaces', loadChildren: () => import('./features/admin/admin-module').then(m => m.AdminModule) }] }
```
**`Shell`** (F12, modified) — `navItems` become `auth.isSuperAdmin() ? [{ label: 'Workspaces', icon: 'domain', link: '/admin/workspaces' }] : [...tenant items]`, and `workspace.current()` is skipped for the super admin (it would 403) with the toolbar showing "Platform admin".

**`features/admin/`** (new, lazy): `admin-module.ts` (route `''` → `AdminWorkspaces`), `admin.service.ts` (`list()`, `create(req)`), `admin-workspaces.ts/.html` (table: name, admin email, time zone, users, created; empty state "No workspaces yet — create the first one"), `create-workspace-dialog.ts/.html` (`MatDialog`: name, admin email, optional time zone `mat-select` over `Intl.supportedValuesOf('timeZone')`; 409 → "name already exists"; success closes and refreshes, with a snackbar "Workspace created. We emailed the admin."). No tenant data is ever requested from these screens.

## Tests (priority — PBI 19.6)
**Test infrastructure (new, reused by every later endpoint test):** make the entry point visible with `public partial class Program { }` at the bottom of `Program.cs`; add package `Microsoft.AspNetCore.Mvc.Testing` to `ZenLead.Tests`; **`Api/Support/ApiFactory.cs`**:
```csharp
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "unused", ["Jwt:SigningKey"] = new string('k', 48), ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test",
            ["OpenAI:ApiKey"] = "test", ["Hangfire:Enabled"] = "false", ["Email:Provider"] = "Log", ["Seed:SuperAdmin"] = "false"
        }));
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<DbContextOptions<ZenLeadDbContext>>();
            s.AddDbContext<ZenLeadDbContext>(o => o.UseSqlite(_connection));
            using var scope = s.BuildServiceProvider().CreateScope();
            scope.ServiceProvider.GetRequiredService<ZenLeadDbContext>().Database.EnsureCreated();
        });
    }
    // helpers: CreateWorkspaceAndUserAsync(name, email) → (workspaceId, accessToken); CreateSuperAdminAsync() → accessToken; ClientFor(token)
}
```
(Registrations that choose the SQL Server provider inside `AddZenLeadHangfire`/`UseSqlServer` are bypassed by `Hangfire:Enabled=false` and `RemoveAll<DbContextOptions<…>>`; **verify** nothing else in `Program.cs` touches SQL Server at startup.)

**`Api/AuthorizationMatrixTests.cs`**
- **Endpoint inventory test:** enumerate `EndpointDataSource` endpoints; every endpoint is either in an explicit `AnonymousAllowList` (`auth/register|login|refresh`; later features add theirs) or carries authorize metadata resolving to the default policy, `AnyUser` or `SuperAdminOnly`. A new controller that forgets `[Authorize]` fails this test. (F29.3 finishes the allow-list.)
- Super admin token: `GET /api/v1/leads`, `/companies`, `/target-profiles`, `/ai/token-usage`, `/workspaces/current` → **403**. Regular user: `GET/POST /api/v1/admin/workspaces` → 403. Anonymous → 401 on both.
- Super admin refresh: the refreshed access token still has `role=SuperAdmin` and no `workspace_id`.

**`Infrastructure/Identity/SuperAdminSeederTests.cs`** (UserManager over SQLite): creates once; second call no-op and **does not reset a changed password**; missing config → skipped in Development, throws in Production unless one exists; weak password (short, contains the email name) rejected; email of an existing workspace user → throws, user not promoted; password never appears in captured log output.

**`Infrastructure/Persistence/NullWorkspaceTests.cs`** — a super-admin-shaped context (`ICurrentWorkspace = null`) returns zero rows from every tenant table (extends F11's isolation test); the `AppUser` check constraint rejects a non-super-admin without a workspace (SQLite enforces CHECK).

**`Application/Admin/CreateWorkspaceUseCaseTests.cs`** — duplicate name (case/whitespace variants) → `DuplicateName`; bad admin email and bad time zone rejected by the validator; success writes an audit row (`workspace.created`, actor id, workspace id) and sends the welcome email via `FakeEmailSender`; a failing email send still returns success; the workspace then appears in `ListWithUserCountsAsync` with `UserCount = 0` (and is what the F20 picker will search).

**`Api/Support/LockoutTests.cs`** — 5 wrong passwords → locked (correct password also refused during lockout, same generic 401).

**Angular:** `jwt-claims.spec.ts` (decode, malformed token → null), `auth.guard.spec.ts` (tenant/super-admin redirects), `login.spec.ts` (super admin lands on `/admin/workspaces`), `admin-workspaces.spec.ts` (create dialog posts, 409 message), `shell.spec.ts` (super admin sees only the Workspaces item and no `workspace.current()` call).

## Not in this feature
Rename/delete workspace, change admin, suspend, MFA (backlog, called out in parent §13), any tenant-data view for the super admin, user listing across workspaces.

## Verification
1. `dotnet ef migrations add AddSuperAdminAndWorkspaceAdmin …`; set `SuperAdmin:Email`/`Password` in user-secrets; start the API → "Super admin account created" in the log (no password); restart → nothing; change the secret's password value and restart → login still works with the **old** password.
2. Log in as the super admin in the SPA → lands on *Workspaces*; open the browser devtools network tab: no calls to `/leads`; manually `GET /api/v1/leads` with that token → 403.
3. Create "Zenrax" with an admin email → appears in the list; the admin email (console, `Email:Provider=Log`) shows the register link; audit row exists in `AuditLogEntries`.
4. Log in as a regular user: `/admin/workspaces` redirects to `/leads`; API `GET /admin/workspaces` → 403.
5. `dotnet test` — including the endpoint-inventory test.
