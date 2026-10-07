# Feature 20 — Workspace Membership & Registration Approval

**Branch:** `feature/workspace-membership`
**Sprint:** 2
**Depends on:** F18 (`IEmailSender`, `SystemEmail`, `AppOptions.PublicBaseUrl` from F19), F19 (workspaces now exist only because the super admin created them; `Workspace.AdminEmail`; the default/`AnyUser` policies; `ApiFactory`). F11 (tenant filter) for the "second user sees the same data" test.

## Goal
Colleagues share one workspace. Registering **no longer creates a usable account or a workspace**: the user picks an existing workspace, is stored as `PendingApproval`, and the workspace admin approves by clicking a link in an email. Pending/rejected/disabled users cannot log in or refresh.

## Design decisions
- **The workspace admin is an email address, not a role.** `Workspace.AdminEmail` receives the approval emails; whoever controls that mailbox can approve. There is no admin permission anywhere else in Phase 2.
- **Bootstrapping the first user:** the super admin creates "Zenrax" with `adminEmail = owner@…`; the owner then registers *with that same address*, the approval email goes to their own mailbox, and they approve themselves. No special case needed — and it proves the loop works.
- **Status defaults to `PendingApproval` in code** (`AppUser.Status`), so any path that creates a user without thinking about status produces a user who cannot sign in. Creators who want an active user say so explicitly (the super-admin seeder, the migration backfill).
- **Approval links never change state on GET.** The link opens an Angular page; a `POST` approves. Mail scanners and link previewers can't approve by prefetching.
- **The token is the credential** (single-use, 7 days, 256-bit random, stored hashed). It is URL-safe base64 (`WebEncoders`), unlike the Phase 1 refresh tokens' standard base64.
- **Existing-email handling is uniform**: `register` answers `202` with the same body whether the address is new, pending, or already active (no account enumeration); the work done is shaped alike (a dummy password hash on the "exists" path).

## Files to add/modify

### Domain / Infrastructure

**`ZenLead.Domain/Enums/AppUserStatus.cs`** (new)
```csharp
namespace ZenLead.Domain.Enums;
public enum AppUserStatus { PendingApproval = 0, Active = 1, Rejected = 2, Disabled = 3 }
```

**`ZenLead.Infrastructure/Persistence/AppUser.cs`** (modified) — add `public AppUserStatus Status { get; set; } = AppUserStatus.PendingApproval;` and `public DateTime CreatedAt { get; set; } = DateTime.UtcNow;`.

**`ZenLead.Domain/Entities/RegistrationApproval.cs`** (new) — **not** an `ITenantEntity`: it is read by an anonymous endpoint (the token is the credential), so the tenant filter cannot apply; the code scopes by token hash.
```csharp
public class RegistrationApproval
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string TokenHash { get; set; } = string.Empty;         // Base64 SHA-256 of the raw token; the raw token is never stored
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }                        // CreatedAt + 7 days
    public DateTime? UsedAt { get; set; }
    public ApprovalDecision? Decision { get; set; }                // Approved | Rejected
}
public enum ApprovalDecision { Approved = 1, Rejected = 2 }
```
EF config: `TokenHash` `HasMaxLength(64)` unique index; index `(UserId, CreatedAt)`; FK to `AspNetUsers` cascade; index `AppUser (WorkspaceId, Status)` (for the pending-cap count). `DbSet<RegistrationApproval>`.

**Migration `AddUserStatusAndRegistrationApproval`** — generated, then hand-edit `Up()` so existing users keep working:
```csharp
migrationBuilder.AddColumn<int>("Status", "AspNetUsers", nullable: false, defaultValue: 1);   // existing Phase 1 users → Active
// …EF then leaves the DB default at 1; drop it so new rows must set the value in code:
migrationBuilder.Sql("DECLARE @c sysname = (SELECT dc.name FROM sys.default_constraints dc JOIN sys.columns col ON col.default_object_id = dc.object_id WHERE dc.parent_object_id = OBJECT_ID('AspNetUsers') AND col.name = 'Status'); IF @c IS NOT NULL EXEC('ALTER TABLE AspNetUsers DROP CONSTRAINT ' + @c);");
```
(No workspace is created or renamed here. Dev databases that still have Phase 1 "workspace per user" data keep working as-is; resetting is fine.)

**`ZenLead.Infrastructure/Identity/SuperAdminSeeder.cs`** (F19, modified) — the created `AppUser` sets `Status = AppUserStatus.Active` explicitly.

**`IdentityService`** (modified)
```csharp
public record UserLookup(Guid Id, AppUserStatus Status, Guid? WorkspaceId, string DisplayName, string Email);
public record CredentialCheck(Guid? UserId, AppUserStatus? Status, bool LockedOut)
{
    public bool Valid => UserId is not null;
}

// IIdentityService additions/changes
Task<UserLookup?> FindByEmailAsync(string email, CancellationToken ct = default);
Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, AppUserStatus status, CancellationToken ct = default);
Task<CredentialCheck> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default);   // lockout-aware (F19)
Task SetStatusAsync(Guid userId, AppUserStatus status, CancellationToken ct = default);
Task<UserClaimsData?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default);                      // UserClaimsData gains Status
Task<UserLookup?> FindByIdAsync(Guid userId, CancellationToken ct = default);                                   // UserLookup also carries Email (used by approval + F21 emails)
Task<int> CountPendingAsync(Guid workspaceId, CancellationToken ct = default);                                  // users with Status = PendingApproval (pending-request cap)
void BurnPasswordHash();                                                                                         // dummy hash to even out timing
```
`ValidateCredentialsAsync` returns `Status` only when the password is **correct** (so a wrong password reveals nothing about the account). `BurnPasswordHash` = `userManager.PasswordHasher.HashPassword(dummyUser, "dummy-password-for-timing")`. `EmailExistsAsync` stays for the seeder/tests.

**`IRefreshTokenService`** gets `Task RevokeAllAsync(Guid userId, CancellationToken ct = default)` (`ExecuteUpdateAsync` setting `RevokedAt` where `UserId == id && RevokedAt == null`); update `FakeRefreshTokenService`.

**`IApprovalRepository`** (Application; impl in Infrastructure) —
```csharp
Task AddAsync(RegistrationApproval approval, CancellationToken ct);
Task<RegistrationApproval?> FindByHashAsync(string tokenHash, CancellationToken ct);
Task<bool> TryConsumeAsync(string tokenHash, DateTime now, ApprovalDecision decision, CancellationToken ct);   // atomic: UPDATE … WHERE UsedAt IS NULL AND ExpiresAt > @now
Task InvalidatePendingForUserAsync(Guid userId, DateTime now, CancellationToken ct);                          // sets ExpiresAt = now on unused rows
Task<DateTime?> LastCreatedAtAsync(Guid userId, CancellationToken ct);
Task<int> CountPendingUsersAsync(Guid workspaceId, CancellationToken ct);                                    // Users where Status = PendingApproval
```
(Counting users lives on the identity side; expose it from `IIdentityService.CountPendingAsync(workspaceId)` instead if keeping repositories tidy.)

### Application

**`Dtos/Auth/AuthDtos.cs`** (modified)
```csharp
public record RegisterRequest(string Email, string Password, string DisplayName, Guid WorkspaceId);
public record RegistrationAcceptedResponse(string Message);
public record ApprovalInfoResponse(string Status, string? RequesterName, string? RequesterEmail, string? WorkspaceName); // Status: pending | used | expired
public record WorkspaceOption(Guid Id, string Name);
```
`RegisterRequestValidator`: remove `WorkspaceName`; add `RuleFor(x => x.WorkspaceId).NotEmpty()`; the password rules stay. `LoginRequest`/`RefreshRequest`/`AuthResponse` unchanged.

**`Auth/RegistrationOptions.cs`** (new, bound from `Registration`): `ApprovalLifetimeDays = 7`, `MaxPendingPerWorkspace = 20`, `ResendAfter = 1h`.

**`UseCases/Auth/RequestRegistrationUseCase.cs`** (new — replaces `RegisterWorkspaceUseCase`, which is deleted along with its tests)
```csharp
public class RequestRegistrationUseCase(
    IWorkspaceRepository workspaces, IIdentityService identity, IApprovalRepository approvals, IEmailSender email,
    IOptions<AppOptions> app, IOptions<RegistrationOptions> options, TimeProvider clock)
{
    public async Task<RegistrationOutcome> ExecuteAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var workspace = await workspaces.GetByIdAsync(request.WorkspaceId, ct);
        if (workspace is null) return RegistrationOutcome.UnknownWorkspace;
        if (string.IsNullOrWhiteSpace(workspace.AdminEmail)) return RegistrationOutcome.WorkspaceNotAccepting;

        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await identity.FindByEmailAsync(request.Email, ct);

        if (existing is not null)
        {
            identity.BurnPasswordHash();                                                    // same cost as the create path
            if (existing.Status == AppUserStatus.PendingApproval && existing.WorkspaceId == workspace.Id
                && (await approvals.LastCreatedAtAsync(existing.Id, ct) ?? DateTime.MinValue) <= now - options.Value.ResendAfter)
                await IssueAndSendAsync(existing.Id, existing.DisplayName, request.Email, workspace, now, ct);   // at most one re-send per hour
            return RegistrationOutcome.Accepted;                                             // identical response in every "exists" case
        }

        if (await identity.CountPendingAsync(workspace.Id, ct) >= options.Value.MaxPendingPerWorkspace)
            return RegistrationOutcome.TooManyPending;

        var userId = await identity.CreateUserAsync(workspace.Id, request.Email, request.Password, request.DisplayName, AppUserStatus.PendingApproval, ct);
        await IssueAndSendAsync(userId, request.DisplayName, request.Email, workspace, now, ct);
        return RegistrationOutcome.Accepted;
    }

    private async Task IssueAndSendAsync(Guid userId, string name, string requesterEmail, Workspace workspace, DateTime now, CancellationToken ct)
    {
        await approvals.InvalidatePendingForUserAsync(userId, now, ct);                      // only the newest link works
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');   // = WebEncoders.Base64UrlEncode
        await approvals.AddAsync(new RegistrationApproval
        {
            Id = Guid.NewGuid(), UserId = userId, WorkspaceId = workspace.Id, TokenHash = TokenHasher.Hash(raw),
            CreatedAt = now, ExpiresAt = now.AddDays(options.Value.ApprovalLifetimeDays)
        }, ct);

        var link = $"{app.Value.PublicBaseUrl.TrimEnd('/')}/approve?token={raw}";
        await email.SendAsync(SystemEmail.Create(workspace.AdminEmail!, null,
            $"{name} wants to join {workspace.Name} on ZenLead",
            $"{name} ({requesterEmail}) has asked to join the “{workspace.Name}” workspace.\nOnly approve people you recognise. The link is valid for {options.Value.ApprovalLifetimeDays} days and can be used once.",
            link, "Review request"), ct);                                                    // failures are logged by the sender; the user can re-request after an hour
    }
}
```
`TokenHasher.Hash(raw)` = Base64 SHA-256 (shared by F21; extract from `RefreshTokenService`'s private `Hash` into `Application/Security/TokenHasher.cs` and use it in both). A `CreateUserAsync` race on the same email (`EmailAlreadyRegisteredException`) is caught and treated as the "exists" path, so concurrency never leaks either.

**`UseCases/Auth/ApprovalUseCases.cs`** (new)
```csharp
public class GetApprovalInfoUseCase(IApprovalRepository approvals, IIdentityService identity, IWorkspaceRepository workspaces, TimeProvider clock)
{   // returns ApprovalInfoResponse: unknown token → null (404); used → {status:"used"}; expired → {status:"expired"}; else details }

public class DecideApprovalUseCase(IApprovalRepository approvals, IIdentityService identity, IRefreshTokenService refreshTokens,
                                   IWorkspaceRepository workspaces, IEmailSender email, IOptions<AppOptions> app, TimeProvider clock)
{
    public async Task<DecisionResult> ExecuteAsync(string rawToken, ApprovalDecision decision, CancellationToken ct)
    {
        var hash = TokenHasher.Hash(rawToken);
        var approval = await approvals.FindByHashAsync(hash, ct);
        if (approval is null) return DecisionResult.NotFound;
        var now = clock.GetUtcNow().UtcDateTime;

        if (!await approvals.TryConsumeAsync(hash, now, decision, ct))             // atomic single use: two clicks / two scanners → one wins
            return approval.UsedAt is not null ? DecisionResult.AlreadyUsed : DecisionResult.Expired;

        var user = await identity.FindByIdAsync(approval.UserId, ct);
        if (user is null || user.Status != AppUserStatus.PendingApproval) return DecisionResult.Done;   // e.g. disabled meanwhile: token burned, no state change

        await identity.SetStatusAsync(user.Id, decision == ApprovalDecision.Approved ? AppUserStatus.Active : AppUserStatus.Rejected, ct);
        if (decision == ApprovalDecision.Rejected) await refreshTokens.RevokeAllAsync(user.Id, ct);     // defensive; a pending user has none
        else await email.SendAsync(SystemEmail.Create(user.Email, user.DisplayName, "You're approved on ZenLead",
                 "Your request to join the workspace was approved. You can sign in now.",
                 $"{app.Value.PublicBaseUrl.TrimEnd('/')}/login", "Sign in"), ct);
        return DecisionResult.Done;
    }
}
```
No email on rejection (generic "not approved" at login covers it, and it avoids us emailing someone on a stranger's behalf).

**`UseCases/Auth/LoginUseCase.cs`** (modified)
```csharp
public enum LoginOutcome { Success, InvalidCredentials, PendingApproval, NotApproved }
public record LoginResult(LoginOutcome Outcome, AuthResponse? Tokens = null);

var check = await identity.ValidateCredentialsAsync(request.Email, request.Password, ct);
if (!check.Valid) return new(LoginOutcome.InvalidCredentials);                      // wrong password, unknown email, locked out — all the same
return check.Status switch
{
    AppUserStatus.Active => /* issue tokens exactly as before */ new(LoginOutcome.Success, tokens),
    AppUserStatus.PendingApproval => new(LoginOutcome.PendingApproval),
    _ => new(LoginOutcome.NotApproved)                                              // Rejected / Disabled
};
```
**`RefreshTokenUseCase`** — after `ValidateAndRotateAsync` succeeds, `claimsData.Status != Active` → `await refreshTokens.RevokeAllAsync(userId)` and return `null` (401). Super admin has `Status = Active` (seeder) so refresh keeps working.

**`IWorkspaceRepository`** (modified) — add `Task<IReadOnlyList<WorkspaceOption>> SearchByNameAsync(string? q, int max, CancellationToken ct)`: `Name.Contains(q)` (escape LIKE wildcards as in F13), order by name, `Take(max)`, `AsNoTracking`, only `Id, Name` projected — **never** `AdminEmail`.

### Api

**`AuthController`** (modified)
```csharp
[HttpPost("register")]
[EnableRateLimiting(RateLimiting.RegisterPolicy)]
public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
{
    var validation = await registerValidator.ValidateAsync(request, ct);
    if (!validation.IsValid) return ValidationProblem(validation.ToModelState());
    try
    {
        return (await requestRegistration.ExecuteAsync(request, ct)) switch
        {
            RegistrationOutcome.Accepted => Accepted(new RegistrationAcceptedResponse("Request received. If the address can be registered, your workspace administrator has been asked to approve it.")),
            RegistrationOutcome.UnknownWorkspace => ValidationProblem(new ModelStateDictionary().With(nameof(request.WorkspaceId), "Choose a workspace from the list.")),
            RegistrationOutcome.WorkspaceNotAccepting => Conflict(new { message = "That workspace is not accepting registrations yet." }),
            RegistrationOutcome.TooManyPending => StatusCode(429, new { message = "Too many pending requests for that workspace. Please try again later." }),
            _ => StatusCode(500)
        };
    }
    catch (RegistrationFailedException ex) { /* password policy errors → 400 as in Phase 1 */ }
    // EmailAlreadyRegisteredException is handled inside the use case; it never reaches here
}

[HttpPost("login")]
public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
{ /* validate */
    var result = await login.ExecuteAsync(request, ct);
    return result.Outcome switch
    {
        LoginOutcome.Success => Ok(result.Tokens),
        LoginOutcome.PendingApproval => StatusCode(403, new { code = "pending_approval", message = "Your account is awaiting approval by your workspace administrator." }),
        LoginOutcome.NotApproved => StatusCode(403, new { code = "not_approved", message = "Your registration was not approved." }),
        _ => Unauthorized()
    };
}

[HttpGet("approvals/{token}")]            public async Task<IActionResult> ApprovalInfo(string token, CancellationToken ct) => /* 404 if unknown */;
[HttpPost("approvals/{token}/approve")]   public Task<IActionResult> Approve(string token, CancellationToken ct) => Decide(token, ApprovalDecision.Approved, ct);
[HttpPost("approvals/{token}/reject")]    public Task<IActionResult> Reject(string token, CancellationToken ct)  => Decide(token, ApprovalDecision.Rejected, ct);
// Decide: Done → 204; NotFound → 404; AlreadyUsed → 409 {code:"already_used"}; Expired → 410 {code:"expired"}
```
`AuthController` keeps `[EnableRateLimiting(AuthPolicy)]` at class level (20/min/IP); `register` also gets the stricter policy.

**`RateLimiting.cs`** — add `RegisterPolicy` (5 requests / 10 minutes per IP) and `PickerPolicy` (30/min per IP); register both in `Program.cs`.

**`WorkspacesController`** (F12/F18, modified) — move `[Authorize]` from the class onto the `current` and `time-zone` actions (default policy), and add:
```csharp
[HttpGet]
[AllowAnonymous]
[EnableRateLimiting(RateLimiting.PickerPolicy)]
public async Task<ActionResult<IReadOnlyList<WorkspaceOption>>> Search([FromQuery] string? q, CancellationToken ct)
    => Ok(await workspaces.SearchByNameAsync(q?.Trim() is { Length: <= 100 } s ? s : null, 20, ct));
```
Anonymous exposure of workspace *names* is accepted while Zenrax is the only tenant (parent §1); revisit before onboarding other organisations. Add this route to the F19 endpoint-inventory **anonymous allow-list**, plus `auth/approvals/**`.

**`Program.cs`** — register `RequestRegistrationUseCase`, `GetApprovalInfoUseCase`, `DecideApprovalUseCase`, `IApprovalRepository`, `Configure<RegistrationOptions>`; remove `RegisterWorkspaceUseCase`. `StartupConfiguration` unchanged here.

**Logging note for F29:** the approval and reset links carry the token in the query string of an SPA page request. Request logging must not record query strings for `/approve` and `/reset-password`, and F29's `Referrer-Policy: no-referrer` header applies (the Angular pages also strip the token from the address bar right after reading it).

### Angular

**`core/auth/auth.service.ts`** — `register()` no longer stores a session:
```ts
register(request: RegisterRequest): Observable<void> {
  return this.http.post<void>('/api/v1/auth/register', request);          // 202, no tokens
}
```
**`auth.models.ts`** — `RegisterRequest { email; password; displayName; workspaceId }`; add `WorkspaceOption { id; name }`.

**`features/auth/register/`** (modified)
- `register.ts`: form `{ workspace: [null as WorkspaceOption | string | null, requiredOption], displayName, email, password }`. Type-ahead: `workspaceCtrl.valueChanges.pipe(debounceTime(250), distinctUntilChanged(), filter(v => typeof v === 'string'), switchMap(q => this.workspaces.search(q)))` → `MatAutocomplete` with `[displayWith]`. Validator `requiredOption` fails unless the value is an object with an `id` (free text is rejected client-side as the server also rejects unknown ids). `submit()` → `auth.register({ ...,  workspaceId: option.id })` → on success `router.navigate(['/register/pending'], { queryParams: { w: option.name } })`; errors via `messageFor`: 429 → "Too many requests, try again later.", 409 → "That workspace isn't accepting registrations yet.", 400 → joined validation messages. Keep the zoneless `cdr.markForCheck()`.
- `register.html`: title "Request access"; replace the *Workspace name* input with the autocomplete (`mat-autocomplete`, `matAutocomplete`, hint "Start typing your organisation's name").
- `core/workspace-picker.service.ts`: `search(q): Observable<WorkspaceOption[]>` → `GET /api/v1/workspaces?q=`.
- `features/auth/register-pending/` (new, declared in `AppModule`; route `register/pending`): static card "Request sent — you'll be able to sign in once the admin of *{{ w }}* approves it. We'll email you." with a *Back to sign in* link.

**`features/auth/approve/`** (new; route `approve`, **public**, declared in `AppModule`)
```ts
export class Approve implements OnInit {
  state: 'loading' | 'ready' | 'approved' | 'rejected' | 'used' | 'expired' | 'invalid' | 'error' = 'loading';
  info: ApprovalInfo | null = null;
  private token: string | null = null;

  ngOnInit(): void {
    this.token = this.route.snapshot.queryParamMap.get('token');
    this.router.navigate([], { replaceUrl: true, queryParams: {} });          // take the token out of the address bar and history
    if (!this.token) { this.state = 'invalid'; return; }
    this.approvals.info(this.token).subscribe({
      next: i => { this.info = i; this.state = i.status === 'pending' ? 'ready' : i.status === 'used' ? 'used' : 'expired'; this.cdr.markForCheck(); },
      error: e => { this.state = e.status === 404 ? 'invalid' : 'error'; this.cdr.markForCheck(); }
    });
  }
  decide(decision: 'approve' | 'reject'): void { /* POST; 204 → approved/rejected; 409 → used; 410 → expired; else error */ }
}
```
The card shows requester name, email, workspace, the warning "Only approve people you recognise", and **Approve** (primary) / **Reject** buttons; after the decision a plain confirmation. No sign-in needed.

**`features/auth/login/login.ts`** (modified) — handle the new 403 body: `err.status === 403 && err.error?.code === 'pending_approval'` → "Your account is awaiting approval by your workspace administrator."; `not_approved` → "Your registration wasn't approved. Contact your workspace administrator."; other → existing "Invalid email or password." Add link "Request access" (was *Register*).

**Routing** (`app-routing-module.ts`): public routes `register`, `register/pending`, `approve`, `login`. **`proxy.conf.js`**: no change.

### Test updates (Phase 1 tests that break)
- **Delete** `RegisterWorkspaceUseCaseTests.cs`; **rewrite** `RegisterRequestValidatorTests.cs` (no workspace name; `WorkspaceId` required); `LoginUseCaseTests.cs` / `RefreshTokenUseCaseTests.cs` (new result types, status checks); `FakeAuthAbstractions.cs` (`FakeIdentityService` with a user dictionary and statuses, `FakeApprovalRepository`, `FakeWorkspaceRepository.SearchByNameAsync`/`GetByIdAsync`).
- Angular: `register.spec.ts` (autocomplete requires a selected option; payload carries `workspaceId`; navigates to pending screen), `login.spec.ts` (403 codes), `auth.interceptor.spec.ts` unchanged, `auth-links.spec.ts` (link text).

## Tests (priority — PBI 20.7)
Use `FakeEmailSender`, a controllable `TimeProvider` (`FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`), and `ApiFactory` for the end-to-end cases.
- **Registration:** unknown workspace id → 400 and no user; workspace without `AdminEmail` → 409; new email → user `PendingApproval`, one approval row, exactly one email to `Workspace.AdminEmail` containing the link and the requester's name/email (HTML-encoded: a display name of `<b>x</b>` is escaped); **same 202 body** for new / pending / active / rejected addresses; duplicate pending request inside an hour sends no second email, after an hour sends one and **invalidates the older token**; pending cap: the 21st distinct request → 429 and no user; weak password → 400; per-IP rate limit → 429 (small window in test config).
- **Approval token:** approve → user `Active` + "you're approved" email to the user; reject → `Rejected`, no email; **replay** of an approved token → 409 and no second state change; expired (advance clock 7 days + 1 s) → 410; wrong/tampered token → 404; two concurrent `approve` calls → exactly one `Done` (use real SQLite + `Task.WhenAll`); GET info never changes state; info for a used/expired token shows the status only (no requester details).
- **Login/refresh:** pending user with correct password → 403 `pending_approval`; with wrong password → plain 401 (no hint); rejected/disabled → 403 `not_approved`; a refresh token issued while Active stops working once the user is set `Disabled` (and tokens are revoked); the super admin (Active, no workspace) is unaffected.
- **Sharing:** after approval the second user's token sees the **same leads** as the first (same `workspace_id`); a user from another workspace still gets 404s — F11's isolation tests stay green with two workspaces.
- **Bootstrapping:** admin registers with `adminEmail == own email`, receives the link on that address, approves, can log in.
- **Picker:** `GET /workspaces?q=zen` returns only `{id,name}` (assert the JSON has no `adminEmail`), max 20, `%`/`_` literal, anonymous OK, rate-limited.
- **Authorization inventory (F19):** the new anonymous routes are on the allow-list; nothing else became anonymous.

## Not in this feature
Admin UI to list/disable users, multiple admins or changing the admin, invite-by-admin, switching workspaces, "resend approval link" button for the admin, email verification of the registrant's own address (the approval step stands in for it), CAPTCHA (rate limits + pending cap instead — revisit if abused).

## Verification
1. Run the migration on a dev DB with Phase 1 users → they're `Active` and can still log in.
2. As super admin create "Zenrax" (admin email = yours). Register from the SPA by typing "zen" and picking it → "request sent" screen; console (`Email:Provider=Log`) prints the approval link.
3. Try to log in → "awaiting approval". Open the link → card shows who asked; **Approve** → login now works and the console shows the "approved" mail. Open the link again → "already used".
4. Register a second user (a colleague address), approve, log in: they see the first user's leads.
5. Disable a user in the DB (`Status = 3`), refresh the SPA → signed out.
6. `dotnet test`, `ng test`.
