# Feature 21 — Forgot & Reset Password

**Branch:** `feature/password-reset`
**Sprint:** 2
**Depends on:** F18 (`IEmailSender`, `SystemEmail`), F19 (`Policies.AnyUser`, `AppOptions.PublicBaseUrl`, `ApiFactory`), F20 (`AppUserStatus`, `TokenHasher`, `IRefreshTokenService.RevokeAllAsync`, `UserLookup`).

## Goal
Forgot-password and reset-password for **every** user including the super admin, plus change-password for signed-in users. No account enumeration, hashed single-use tokens, all sessions revoked on reset.

## Design decisions
- **Own token table, not ASP.NET Identity's data-protection tokens.** Identity's `GeneratePasswordResetTokenAsync` depends on a persistent Data Protection key ring; on Azure App Service that ring is per-instance unless configured, so links would break after a restart. A hashed random token in our own table has no such dependency (and matches how F20 approvals work). The password itself is set through `UserManager` (`RemovePasswordAsync` + `AddPasswordAsync`), which still applies Identity's policy.
- **Uniform response and timing for forgot-password.** The "account exists" path does extra work (a DB write and an HTTP call to the mail provider), so the response is **padded to a minimum duration plus a little jitter** (`PasswordReset:MinResponseMs`, default 600) on *every* path. The email is deliberately not sent from a background job to equalise timing: a job would put the raw reset link into Hangfire's job storage in plaintext, defeating the point of hashing the token in our own table. Residual risk: if the mail provider is slower than the padding, existing accounts become measurably slower — acceptable at this scale; revisit with a short-lived outbox if it matters.
- **Policy is validated before the token is consumed**, so typing a weak password doesn't burn the link. The token is consumed atomically before the password is written, so two concurrent submissions can't both succeed.
- **Reset ends all sessions**: every refresh token is revoked; existing 15-minute access tokens simply expire. Change-password revokes all too, then hands the caller a fresh pair so they stay signed in.
- **Pending/Rejected/Disabled users get nothing**: no email, no token row.

## Files to add/modify

### Domain / Infrastructure

**`ZenLead.Domain/Entities/PasswordResetToken.cs`** (new) — not an `ITenantEntity` (anonymous flow, token is the credential).
```csharp
public class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;     // Base64 SHA-256; raw token only ever lives in the email
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }                    // CreatedAt + 1 hour
    public DateTime? UsedAt { get; set; }
    public string? RequestedIp { get; set; }                   // max 45 chars (IPv6); for abuse investigation only
}
```
EF config: `TokenHash` max 64 + unique index, `RequestedIp` 45, index `(UserId, CreatedAt)`, FK to `AspNetUsers` cascade. `DbSet<PasswordResetToken>`. Migration **`AddPasswordResetTokens`**. Housekeeping: `IssueAsync` deletes the user's tokens older than 7 days (same pattern as `RefreshTokenService.IssueAsync`).

**`IPasswordResetRepository`** (Application; EF impl in Infrastructure)
```csharp
Task AddAsync(PasswordResetToken token, CancellationToken ct);
Task<int> CountCreatedSinceAsync(Guid userId, DateTime since, CancellationToken ct);
Task InvalidateUnusedAsync(Guid userId, DateTime now, CancellationToken ct);                      // ExpiresAt = now where UsedAt IS NULL AND ExpiresAt > now
Task<PasswordResetToken?> FindByHashAsync(string hash, CancellationToken ct);
Task<bool> TryConsumeAsync(string hash, DateTime now, CancellationToken ct);                      // atomic UPDATE … WHERE UsedAt IS NULL AND ExpiresAt > @now
```

**`IIdentityService`** (modified — F20 added `FindByEmailAsync`, `FindByIdAsync`, `SetStatusAsync`)
```csharp
Task<IReadOnlyList<string>> ValidatePasswordAsync(Guid userId, string password, CancellationToken ct = default);   // runs UserManager.PasswordValidators; empty = ok
Task SetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default);                              // RemovePassword + AddPassword, ResetAccessFailedCount, clear lockout, UpdateSecurityStamp
Task<ChangePasswordResult> ChangePasswordAsync(Guid userId, string current, string next, CancellationToken ct = default);
```
`ChangePasswordResult`: `Success | WrongCurrentPassword | LockedOut | PolicyFailed(IReadOnlyList<string> errors)`. Implement `ChangePasswordAsync` with `CheckPasswordAsync` + the lockout counters from F19 (wrong current password counts as a failed attempt, so the change form can't be used to guess the password).

**Shared password policy.** Extract the rules out of `RegisterRequestValidator` into `ZenLead.Application/Validation/PasswordRules.cs`:
```csharp
public static class PasswordRules
{
    public static IRuleBuilderOptions<T, string> MustBeStrongPassword<T>(this IRuleBuilder<T, string> rule)
        => rule.NotEmpty().MinimumLength(8)
               .Matches("[0-9]").WithMessage("Password must contain a digit.")
               .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
               .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
               .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain a non-alphanumeric character.");
}
```
`RegisterRequestValidator` uses it (behaviour unchanged); the two new validators use it too. Keep it in sync with the Identity options in `Program.cs` (comment already says so).

### Application

**`Dtos/Auth/AuthDtos.cs`** (modified)
```csharp
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
```
Validators: `ForgotPasswordRequestValidator` (`Email` not empty, valid, ≤ 256), `ResetPasswordRequestValidator` (`Token` not empty, ≤ 200; `NewPassword.MustBeStrongPassword()`), `ChangePasswordRequestValidator` (current not empty; new strong; new ≠ current).

**`Auth/PasswordResetOptions.cs`** — `TokenLifetime = 1h`, `MaxPerEmailPerHour = 3`, `MinResponseMs = 600`.

**`UseCases/Auth/ForgotPasswordUseCase.cs`** (new)
```csharp
public class ForgotPasswordUseCase(IIdentityService identity, IPasswordResetRepository tokens, IEmailSender email,
    IOptions<AppOptions> app, IOptions<PasswordResetOptions> options, TimeProvider clock)
{
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);   // tests: no-op/recorder

    /// <summary>Always completes the same way for the caller; the only observable effect of an eligible account is the email.</summary>
    public async Task ExecuteAsync(string emailAddress, string? requestedIp, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        try { await TryIssueAsync(emailAddress, requestedIp, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { /* never reveal failures (e.g. mail provider down) to an anonymous caller; sender already logs */ }
        var remaining = TimeSpan.FromMilliseconds(options.Value.MinResponseMs) - Stopwatch.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero) await Delay(remaining + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100)), ct);   // pad + jitter
    }

    private async Task TryIssueAsync(string emailAddress, string? ip, CancellationToken ct)
    {
        var user = await identity.FindByEmailAsync(emailAddress, ct);
        if (user is null || user.Status != AppUserStatus.Active) return;                        // pending/rejected/disabled/unknown: silent

        var now = clock.GetUtcNow().UtcDateTime;
        if (await tokens.CountCreatedSinceAsync(user.Id, now.AddHours(-1), ct) >= options.Value.MaxPerEmailPerHour) return;   // throttled: silent

        await tokens.InvalidateUnusedAsync(user.Id, now, ct);                                    // only the newest link works
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await tokens.AddAsync(new PasswordResetToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, TokenHash = TokenHasher.Hash(raw),
            CreatedAt = now, ExpiresAt = now.Add(options.Value.TokenLifetime), RequestedIp = ip?[..Math.Min(ip.Length, 45)]
        }, ct);

        await email.SendAsync(SystemEmail.Create(user.Email, user.DisplayName, "Reset your ZenLead password",
            "Someone asked to reset the password for this account. If that was you, use the link below within 1 hour. If not, you can ignore this email — your password hasn't changed.",
            $"{app.Value.PublicBaseUrl.TrimEnd('/')}/reset-password?token={raw}", "Choose a new password"), ct);
    }
}
```
`FindByEmailAsync` must return the user's **email as stored** and tolerate casing; the super admin (`Status = Active`, no workspace) is eligible like anyone else.

**`UseCases/Auth/ResetPasswordUseCase.cs`** (new)
```csharp
public async Task<ResetResult> ExecuteAsync(ResetPasswordRequest request, CancellationToken ct = default)
{
    var hash = TokenHasher.Hash(request.Token);
    var row = await tokens.FindByHashAsync(hash, ct);
    var now = clock.GetUtcNow().UtcDateTime;
    // generic failure for: unknown, used, expired — the caller can't tell which
    if (row is null || row.UsedAt is not null || row.ExpiresAt <= now) return ResetResult.InvalidToken;

    var errors = await identity.ValidatePasswordAsync(row.UserId, request.NewPassword, ct);   // BEFORE consuming: typos don't burn the link
    if (errors.Count > 0) return ResetResult.WeakPassword(errors);

    if (!await tokens.TryConsumeAsync(hash, now, ct)) return ResetResult.InvalidToken;       // lost a race with another submit
    var user = await identity.FindByIdAsync(row.UserId, ct);
    if (user is null || user.Status != AppUserStatus.Active) return ResetResult.InvalidToken; // disabled since the email went out

    await identity.SetPasswordAsync(user.Id, request.NewPassword, ct);
    await refreshTokens.RevokeAllAsync(user.Id, ct);                                          // every session signed out
    await email.SendAsync(SystemEmail.Create(user.Email, user.DisplayName, "Your ZenLead password was changed",
        "The password for your account was just changed. If this wasn't you, reset it again now and tell your workspace administrator.",
        $"{app.Value.PublicBaseUrl.TrimEnd('/')}/forgot-password", "Reset password"), ct);
    return ResetResult.Ok;
}
```
**`UseCases/Auth/ChangePasswordUseCase.cs`** (new) — `identity.ChangePasswordAsync(...)`; on success `refreshTokens.RevokeAllAsync(userId)`, then `refreshTokens.IssueAsync(userId)` + a new access token (so the caller's session continues), the same notice email, returns `AuthResponse`; `WrongCurrentPassword`/`LockedOut` → 400 `{code:"wrong_password"}` (same code for both; lockout is invisible here).

### Api

**`AuthController`** (modified)
```csharp
[HttpPost("forgot-password")]
[EnableRateLimiting(RateLimiting.ForgotPolicy)]
public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
{
    var validation = await forgotValidator.ValidateAsync(request, ct);
    if (!validation.IsValid) return ValidationProblem(validation.ToModelState());       // only for syntactically invalid input — says nothing about accounts
    await forgot.ExecuteAsync(request.Email, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
    return Ok(new { message = "If an account exists for that address, we've emailed a reset link." });
}

[HttpPost("reset-password")]
public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    => (await reset.ExecuteAsync(request, ct)) switch
    {
        { IsOk: true } => NoContent(),
        { Errors: { Count: > 0 } e } => ValidationProblem(/* NewPassword → e */),
        _ => BadRequest(new { code = "invalid_token", message = "This link is invalid or has expired. Request a new one." })
    };

[HttpPost("change-password")]
[Authorize(Policy = Policies.AnyUser)]                  // works for the super admin too
public async Task<ActionResult<AuthResponse>> ChangePassword(ChangePasswordRequest request, CancellationToken ct) { /* UserId() from "sub" */ }
```
**`RateLimiting.cs`** — `ForgotPolicy`: 10 requests/hour per IP (the per-email cap lives in the use case). Class-level `AuthPolicy` (20/min/IP) still applies. Add `auth/forgot-password`, `auth/reset-password` to the F19 anonymous allow-list; `change-password` is `AnyUser`. `Program.cs`: register the three use cases, `IPasswordResetRepository`, `Configure<PasswordResetOptions>`.

> **Referrer/log hygiene** — same note as F20: no query-string logging for `/reset-password`, `Referrer-Policy: no-referrer` (F29).

### Angular

**`features/auth/forgot-password/`** (new, public route `forgot-password`) — one email field → `POST /auth/forgot-password`; after **any** 200, swap the form for the neutral confirmation ("If an account exists… check your inbox. The link works for 1 hour."); 429 → "Too many requests. Try again later."; no hint about whether the address exists. Login template gets a **Forgot password?** link.

**`features/auth/reset-password/`** (new, public route `reset-password`)
```ts
ngOnInit(): void {
  this.token = this.route.snapshot.queryParamMap.get('token');
  this.router.navigate([], { replaceUrl: true, queryParams: {} });          // remove the token from the address bar and history
  if (!this.token) this.state = 'invalid';
}
submit(): void {                                                            // form: newPassword + confirm (match validator), same rules as register
  this.auth.resetPassword(this.token!, this.form.value.newPassword).subscribe({
    next: () => { this.state = 'done'; this.cdr.markForCheck(); },          // "Password changed. Sign in with your new password." → /login
    error: e => { this.state = e.error?.code === 'invalid_token' ? 'invalid' : 'form'; this.errorMessage = Register.messageFor(e); this.cdr.markForCheck(); }
  });
}
```
`invalid` state: "This link is invalid or has expired" + **Request a new link** → `/forgot-password`. The token stays only in the component instance (memory). Also `this.auth.logout()` on success so a stale in-memory session in that tab can't linger.

**`core/auth/auth.service.ts`** — `forgotPassword(email)`, `resetPassword(token, newPassword)`, `changePassword(current, next): Observable<AuthResponse>` (stores the returned session).

**`features/account/change-password/`** (new, lazy `AccountModule`; route `/account/password` under `Shell` with `authGuard`, reachable by both roles — add the route to the **top-level** shell group and to the admin shell's menu) — form current/new/confirm, success snackbar "Password changed. Other devices were signed out." The **Shell account menu** (F12) gains *Change password*.

## Tests (priority — PBI 21.6)
- **Forgot:** existing active user → one email with a `…/reset-password?token=` link, a token row with `ExpiresAt = now+1h`, only a **hash** stored (assert the raw token isn't in the table and *is* in the email); unknown email → no email, no row, **same HTTP status and body**; pending/rejected/disabled → same; super admin → email sent; second request invalidates the first token (`ExpiresAt` collapsed) and only the newest works; 4th request in an hour → no email, same response; the `Delay` hook is invoked for **both** the exists and unknown paths with the padded duration (timing-*shape* test, not wall-clock); email-provider failure still returns the neutral response; HTTP-level: validation errors only for malformed input.
- **Reset:** happy path → login works with the new password, **old password fails**, every refresh token for the user is revoked (`RefreshTokenService.ValidateAndRotateAsync` on a previously issued token → fails), notice email sent; **replay** after success → generic invalid; expired (advance `FakeTimeProvider` 1 h + 1 s) → generic invalid; tampered/unknown/empty token → generic invalid (identical body to expired — assert equality); weak password → 400 with policy messages **and the token still works afterwards**; two concurrent submits with the same token → exactly one succeeds; token for a user disabled after the email went out → invalid; lockout cleared after reset.
- **Change password:** wrong current → 400 `wrong_password` and counts toward lockout (5 attempts → locked, same response); success returns a working new session, old refresh tokens dead, notice email sent; new == current rejected; weak new rejected; **super admin can change theirs**; anonymous → 401.
- **Policy parity:** a table test that `PasswordRules` and the Identity options in `Program.cs` accept/reject the same samples.
- Angular: `forgot-password.spec.ts` (neutral text on 200 and on 404/500-less cases; no per-account hint), `reset-password.spec.ts` (token read then removed from the URL; mismatch validator; invalid-link state), `auth.service.spec.ts` (`changePassword` stores the session).

## Not in this feature
Email-change flow, "log out everywhere" button, MFA, password expiry/history, admin-triggered resets for others.

## Verification
1. Run the migration; `Email:Provider=Log`. Request a reset for a real user → console shows the link; for `nobody@example.com` → identical UI text and similar latency (check the Network tab: both ≈ 600–700 ms).
2. Open the link: the address bar is cleaned immediately; set a weak password → error, link still valid; set a good one → success page; old password is rejected, new works; a second browser still logged in with the old session is signed out on its next refresh.
3. Open the same link again → "invalid or expired". Request 4 resets quickly → only the first 3 produce console emails.
4. Log in as the super admin → *Change password* works; forgot-password for the super admin email works.
5. `dotnet test`, `ng test`.
