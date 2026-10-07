# Feature 29 — Observability, Security & Reliability Hardening

**Branch:** `feature/mvp-hardening`
**Sprint:** 5 (runs in parallel with F28; protect **29.3** from being cut — parent plan §8)
**Depends on:** all feature work being merged or at least stable (F11–F28). Azure-only items (App Insights sink/alerts, PITR test restore, lifecycle verification) live in F30.

## Goal
Turn "works on my machine" into "safe to put on the internet": structured logging with no personal data or secrets, a security pass (headers/CSP, rate limits, uploads, webhooks, dependencies, repo history), proof that **every** endpoint is either authenticated or deliberately public and **every tenant endpoint has a cross-workspace test**, data hygiene jobs, performance sanity at realistic volumes, a UI error/empty/loading audit with proper session-expiry behaviour, and the global (all-workspace) spend caps.

## Work items (one PBI each; commit separately so the PR reviews cleanly)

### 29.1 — Serilog end-to-end, no PII in logs

**Packages:** `ZenLead.Api` → `Serilog.AspNetCore`, `Serilog.Sinks.File`, `Serilog.Settings.Configuration`. (The Application Insights sink is added in F30 behind config.)

**`ZenLead.Api/Program.cs`** (modified)
```csharp
builder.Host.UseSerilog((context, services, cfg) => cfg
    .ReadFrom.Configuration(context.Configuration)                       // levels/sinks overridable per environment
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("App", "ZenLead")
    .WriteTo.Console()
    .WriteTo.File("logs/zenlead-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true));

// … after UseAuthentication/UseAuthorization so the user is known:
app.UseMiddleware<LogContextMiddleware>();
app.UseSerilogRequestLogging(o =>
{
    o.MessageTemplate = "HTTP {RequestMethod} {SafePath} responded {StatusCode} in {Elapsed:0.0} ms";
    o.EnrichDiagnosticContext = (diag, http) => diag.Set("SafePath", LogRedaction.SafePath(http.Request.Path));   // path only, never the query string
    o.GetLevel = (http, _, ex) => ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
                                : http.Response.StatusCode >= 400 ? LogEventLevel.Warning : LogEventLevel.Information;
});
```
**`appsettings.json`** `Serilog` section: `MinimumLevel.Default = Information`, overrides `Microsoft.AspNetCore = Warning`, `Microsoft.EntityFrameworkCore = Warning` (EF at `Information` would log SQL *parameters*? — it doesn't by default, but keep `EnableSensitiveDataLogging` **off** and assert it in a test), `Hangfire = Information`. `logs/` added to `.gitignore`.

**`ZenLead.Api/Logging/LogContextMiddleware.cs`** (new) — pushes `WorkspaceId` and `UserId` (the `sub` GUID) from claims into `LogContext`; **never** the email or name.

**`ZenLead.Api/Logging/LogRedaction.cs`** (new, pure) — URLs and paths that carry credentials:
```csharp
public static class LogRedaction
{
    private static readonly string[] TokenPrefixes =
        ["/api/v1/unsubscribe/", "/unsubscribe/", "/api/v1/auth/approvals/"];     // tokens live in the path here

    public static string SafePath(PathString path)
    {
        var p = path.Value ?? "/";
        foreach (var prefix in TokenPrefixes)
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return prefix + "{token}" + (p.EndsWith("/approve") ? "/approve" : p.EndsWith("/reject") ? "/reject" : "");
        return p;                                                                  // query strings (?token=, ?secret=) are never logged: SafePath is path-only
    }
}
```
**Rules enforced by review + tests:** log ids (`WorkspaceId`, `EnrollmentId`, `MessageId`), counts and failure reasons; **never** email addresses, names, message/reply bodies, subjects, tokens, passwords, API keys, or `OutboundEmail`/`InboundEmail` objects. The Phase 1 `compose-email` log line that includes `LeadEmail` (`EmailComposer` — `"…for lead {LeadEmail}"`) is changed to the lead *id* (add `LeadId` to `EmailComposeContext` or log nothing lead-specific); `EfTokenUsageTracker` already logs only ids/counts. `LogEmailSender` (dev) intentionally logs bodies — guarded by the F18 startup rule (non-Development must use SendGrid).

### 29.2 — Security pass

| Area | Change | Verification |
|---|---|---|
| **Security headers** (`Security/SecurityHeadersMiddleware.cs`) | `X-Content-Type-Options: nosniff`; `Referrer-Policy: no-referrer` (approval/reset tokens sit in SPA URLs); `X-Frame-Options: DENY` + CSP `frame-ancestors 'none'`; `Permissions-Policy: camera=(), microphone=(), geolocation=()`; `Strict-Transport-Security: max-age=31536000; includeSubDomains` **only when not Development**; `Cache-Control: no-store` on `/api/**` responses | header test via `ApiFactory`; securityheaders-style manual check after F30 |
| **CSP for the SPA** | `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`. Angular Material injects inline styles, hence `'unsafe-inline'` for **styles only**; scripts stay strict (enable Angular's `security.autoCsp` in `angular.json` so the build emits hashes for any inline script, **verify** the build output). The Google-hosted icon font from F12 conflicts with `font-src 'self'`: **self-host it** (`@fontsource/material-icons` or `material-symbols`) and drop the `<link>` in `index.html` — preferable to loosening CSP. Apply CSP only to non-`/api` responses; report-only first (`Content-Security-Policy-Report-Only`) for one pass through every screen, then enforce | browse every screen with devtools open: zero CSP violations; unit test the header builder |
| **Rate limits** | Review the table and fix gaps: `auth` 20/min/IP (class-level), `register` 5/10 min/IP, `forgot` 10/h/IP, `picker` 30/min/IP, `unsubscribe` 30/min/IP, `upload` 5/min/workspace, `discovery` 5/min/workspace, `compose` 10/min/workspace, `reply` 30/min/workspace. Add the **missing**: webhooks (`inbound`/`events` 600/min per source IP — generous, only to cap abuse), `GET approvals/{token}` (inherits `auth`). Rejections return `429` + `Retry-After` (already configured). Behind App Service the client IP must come from `X-Forwarded-For`: add `ForwardedHeaders` middleware (`XForwardedFor | XForwardedProto`, **known proxies/networks configured**, not wide-open) — otherwise every request shares the load balancer's IP and one user can lock out everyone | integration test with forwarded-header config; test that two IPs get separate buckets |
| **Lockout & passwords** | Confirm F19's lockout (5 failures/15 min) covers login and change-password; password policy single source (`PasswordRules` ↔ Identity options) with the F21 parity test | existing tests + one lockout matrix test |
| **Uploads** | Re-verify F15 limits end to end: 10 MB cap enforced by Kestrel (`MaxRequestBodySize` also set globally to 12 MB for non-webhook routes), `.csv` extension **and** content sniff, row/column caps, formula-injection guard on the error export, blob names generated server-side only | fuzz test: 20 malformed/binary/oversized/zip-bomb-ish uploads → 400/413, never 500, no blob left behind |
| **Webhook auth** | Inbound: secret compare is constant-time, secret missing in config ⇒ endpoint returns 401 (not open). Events: signature on raw bytes (F28). Both: no body parsing before auth; request size limits; secret never logged (29.1 path-only logging) | tests exist in F25/F28; add a "secret unset ⇒ 401" case |
| **CORS / cookies / hosts** | No CORS policy registered (SPA is same-origin — assert the absence). `zl_ops` cookie flags checked (HttpOnly, Secure, SameSite=Strict, Path=/hangfire). `AllowedHosts` set to the real hostnames in production config (F30), not `*` | header/cookie tests |
| **Dependencies** | `dotnet list ZenLead.slnx package --vulnerable --include-transitive` and `npm audit --omit=dev` in `ZenLead.Client` and `tools/gate1-rehearsal`; fix or document each finding (accepted-risk list in `docs/security-review.md` with owner and date). Add both commands to the F30 CI as non-blocking warnings for Medium, **blocking for High/Critical** | clean report attached to the PR |
| **Secrets in repo & history** | `gitleaks detect --source . --log-opts="--all"` (or `git log -p --all` grep for `sk-`, `SG.`, `eyJ`, `Password`, connection strings, `SigningKey`); verify `.gitignore` covers `logs/`, `*.user`, `appsettings.*.local.json`; confirm `appsettings*.json` and `ZenLead.Api.http` hold no secrets; rotate anything found, even if "only dev" | scan output committed to `docs/security-review.md` (no secret values) |
| **Hangfire dashboard** | Re-test the F14 cookie gate: no cookie ⇒ blocked, tampered/expired ⇒ blocked, non-admin ⇒ 403 on session issue; dashboard is **read-only in production** (`IsReadOnlyFunc = _ => !env.IsDevelopment()`) | existing F14 tests + the flag |

Write the outcome (findings, fixes, accepted risks) to **`docs/security-review.md`**.

### 29.3 — Authorization sweep (not to be cut)

**`ZenLead.Tests/Api/AuthorizationInventoryTests.cs`** — finalises the F19 test.
1. Enumerates every endpoint from `EndpointDataSource`; each must resolve to exactly one of: **tenant** (default policy), **`AnyUser`**, **`SuperAdminOnly`**, or be in `AnonymousAllowList`. The allow-list is a literal list in the test, each entry with a one-line *why*:
   `POST auth/register|login|refresh|forgot-password|reset-password` (credential endpoints), `GET auth/approvals/{token}` + `POST …/approve|reject` (token is the credential), `GET workspaces` (picker; accepted exposure, parent §1), `GET|POST unsubscribe/{token}` (signed token), `POST webhooks/sendgrid/inbound` (secret), `POST webhooks/sendgrid/events` (signature), `GET health` (F30).
2. Writes the resulting table to **`docs/api-authorization-matrix.md`** (route, verb, policy) — regenerated by the test when `UPDATE_MATRIX=1`; otherwise the test **fails if the committed file differs**, so any new/changed endpoint shows up in the PR diff.
3. Anonymous endpoints must also carry a rate-limit policy (reflection check) — `webhooks` and `unsubscribe` included.

**`ZenLead.Tests/Api/CrossWorkspaceCoverageTests.cs`** — enforces PBI 11.9's "every new tenant endpoint gets a cross-workspace test case".
- `TenantEndpointManifest` (a `static` list in the test project): `{ Route, Verb, TestName }` for every **tenant** endpoint that reads or writes workspace data (leads, companies, target profiles, discovery runs/credits, imports, campaigns & steps & enrollments, inbox, analytics, AI usage, `workspaces/current`).
- Test A: the manifest covers **every** tenant endpoint from the inventory (new endpoint without an entry ⇒ failure naming it).
- Test B: each manifest `TestName` resolves by reflection to an existing `[Fact]`/`[Theory]` method (typos/removed tests ⇒ failure).
- Where an endpoint genuinely has no cross-tenant dimension (e.g. `ai/token-usage` is derived from the claim), the manifest entry says `NotApplicable("derived from claim")` — a conscious, reviewable choice.
- **Backfill:** this PBI writes any missing cross-workspace tests it discovers (most exist per feature docs; expect a handful, e.g. companies search, credits, import errors download, enrollments list, unread-count).

### 29.4 — Data safety & hygiene

- **`docs/migrations-runbook.md`** (new): policy = **forward-only in production** (fix-forward or restore from Azure PITR, never run `Down` on live data); how to produce a SQL script for review (`dotnet ef migrations script <from> <to> --idempotent -p ZenLead.Infrastructure -s ZenLead.Api`); the F30 pipeline applies a **migrations bundle**; a table of every Phase 2 migration with its risk and rollback note (e.g. `AddCompanyAndLeadProvenance`: unique index + lowercase pass, fails loudly on duplicates — rollback = drop index; `AddSuperAdminAndWorkspaceAdmin`: unique workspace name + CHECK constraint; `AddUserStatusAndRegistrationApproval`: backfills `Active`). Each `Down()` is reviewed once and marked *tested on a copy* or *not safe — restore instead*.
- **`Jobs/MaintenanceJob.cs`** (new, daily recurring, `[DisableConcurrentExecution]`): deletes `ProcessedWebhookEvent` > 30 d, `InboundQuarantine` > 30 d, expired/revoked `RefreshToken` > 14 d, used/expired `RegistrationApproval` and `PasswordResetToken` > 30 d; logs counts only. Batched deletes (`ExecuteDeleteAsync` with `Take`-style loop of 5,000) so it never holds long locks. Registered in `Program.cs` next to the sender (`Cron.Daily`). Tests: rows older/younger than the thresholds, batching, no tenant rows touched.
- **Soft-delete audit:** confirm every query that should hide deleted leads does (the named `SoftDelete` filter) and that campaign/inbox history still shows deleted leads' names (projections use `IgnoreQueryFilters([SoftDelete])` deliberately — a test with a deleted lead in a thread).

### 29.5 — Performance sanity (measured, not guessed)

SQLite is not representative; use **LocalDB (SQL Server)**. Add `tools/perf-seed/` (small console project, not in CI) and `ZenLead.Tests/Perf/PerfTests.cs` marked `[Trait("Category","Perf")]`, skipped unless `PERF=1` and `ConnectionStrings:Perf` is set.
- **Seeder** (`SqlBulkCopy`): 1 workspace with **50,000 leads** (20k companies), 200 campaigns-worth of enrollments, **1,000 due enrollments** for the sender, **10,000 inbox threads** with 3 messages each, 100k `EmailMessage`s spread over 90 days.
- **Budgets (server-side, warm):** leads list page (default sort, and `q=` search, and `status` filter) **p95 < 300 ms**; `sourceRunId` filter < 150 ms; inbox thread list (unread filter) < 300 ms; `unread-count` < 30 ms; analytics workspace summary over 30 days < 500 ms; **sender run over 1,000 due enrollments with a fake sender/composer completes < 60 s** and issues a bounded number of queries per enrollment (assert ≤ ~12 via an EF command interceptor — the F23 design loads one context per enrollment); campaign list with counts = 1–2 queries.
- **Method:** capture SQL with an `IDbCommandInterceptor`/`LogTo`, look for N+1 (`COUNT` of commands per request), check the actual plans for table scans on the hot paths and add/adjust indexes (`(WorkspaceId, Status, CreatedAt)` etc.) in a single migration **`TuneIndexes`** with each index justified by a measurement in the PR description. Name search `LIKE '%x%'`: if it breaks the budget at 50k rows, switch to `LIKE 'x%'` for the name/email prefix **plus** an indexed `NormalizedEmail` contains-on-domain, or full-text — decide from the numbers, not upfront.
- Output: numbers recorded in `docs/perf-baseline.md` (hardware, dataset, p50/p95 per endpoint) so F30/F31 can compare against Azure.

### 29.6 — Error/empty/loading states, session expiry, accessibility

**Global HTTP behaviour — `core/http/error.interceptor.ts`** (new, registered after `AuthInterceptor`)
```ts
// 401 on an API call: AuthInterceptor already tries ONE silent refresh; if that fails → sessionExpired()
// 403: "You don't have access to that." snackbar (no retry); 429: "Too many requests — try again in a moment." using Retry-After;
// 0 / 5xx on GET: snackbar "Couldn't reach the server." with a Retry action; mutating requests are never auto-retried.
export function sessionExpired(auth: AuthService, router: Router, snack: MatSnackBar, returnUrl: string): void {
  auth.logout();
  snack.open('Your session has expired. Please sign in again.', 'Dismiss', { duration: 8000 });
  router.navigate(['/login'], { queryParams: { returnUrl } });
}
```
`Login` honours `returnUrl` (only same-origin relative paths: `startsWith('/') && !startsWith('//')` — open-redirect guard, unit-tested) and the `tryRestoreSession` failure path uses the same message. In-flight refresh de-duplication (one refresh for N parallel 401s) is verified in `auth.interceptor.spec.ts` (extend if missing).

**Screen audit checklist** → `docs/ui-state-audit.md`, one row per screen × {loading, empty, error+retry, validation errors, long content/overflow, 360 px width, keyboard-only path, focus after dialogs, dark mode}. Screens: login, register, register-pending, approve, forgot/reset/change password, unsubscribe, leads list, lead detail, add-lead dialog, import wizard (each step), target profiles list/edit/run dialog, campaigns list/detail (each tab), enroll dialog, activation dialogs, inbox (list/conversation/composer), analytics (dashboard/campaign), admin workspaces. Every ✗ is fixed or ticketed with a reason; **no screen may show a blank page, an endless spinner, or a raw error object**.

**Accessibility basics**
- Add `axe-core` as a dev dependency and a tiny helper `testing/a11y.ts` (`expectNoViolations(fixture.nativeElement)` running axe with rules limited to what jsdom supports: names/roles/labels/aria validity/landmarks — not colour contrast). Wire it into the specs of: login, register, leads list, import wizard, campaign step editor, inbox page, analytics dashboard.
- Manual pass: tab order, visible focus rings, dialogs return focus to the trigger, `aria-live` on async status text (import progress, polling results, snackbars), colour never the only signal (chips have text/icons — verified in F13/F24/F27/F28), minimum 4.5:1 contrast in both themes (check with the browser's contrast tooling), `prefers-reduced-motion` respected, page `<title>` updated per route (`TitleStrategy`/route `title`).

### 29.7 — Global spend caps and abuse limits

- **AI:** `IAiBudget` (F23) becomes a composite: per-workspace check (existing) **then** a global check against `AiBudget:GlobalMonthlyCostCapUsd` (sum of `AiUsageLog.EstimatedCostUsd` for the UTC month across all workspaces — an explicit cross-tenant aggregate using `IgnoreQueryFilters`, cached 60 s in `IMemoryCache` so it isn't a query per AI call). `Exceeded` at either level ⇒ the existing friendly path; the message distinguishes "workspace budget" from "platform budget" only in logs, not to users.
- **Discovery credits:** `LeadSource:GlobalMonthlyCreditCap` enforced in `ProcessDiscoveryRunUseCase`/`StartDiscoveryRunUseCase` the same way (sum of `LeadDiscoveryRun.CreditsUsed` this month, all workspaces).
- **Review the registration surface:** `register` per-IP limit, pending-per-workspace cap (20), resend throttle, picker rate limit — add a test that 30 distinct registrations from one IP are cut off while a second IP is unaffected; confirm the admin-email flood protection (max pending + resend rule) end to end.
- Tests: global cap stops AI calls **across workspaces** (A and B each under their own cap, together over the global one → both blocked); month rollover; global check failure (DB error) **fails closed for AI spend** (no call) but never blocks unrelated endpoints; discovery equivalents.

## Tests summary (beyond those inline above)
`LogRedactionTests` (tokens in unsubscribe/approval paths redacted, query string absent, normal paths untouched), `LogContextMiddlewareTests` (adds ids, never email), a captured-log test over a full register→approve→send→reply happy path asserting **no log line contains the test addresses, bodies, tokens or secret** (the single most valuable PII test), `SecurityHeadersTests`, `ForwardedHeadersTests`, `MaintenanceJobTests`, `AuthorizationInventoryTests`, `CrossWorkspaceCoverageTests`, `CompositeAiBudgetTests`, `UploadFuzzTests`, Angular `error.interceptor.spec.ts` (401→logout+returnUrl, 403 toast, 429 message with Retry-After, 5xx GET retry action, POST never retried), `login.spec.ts` returnUrl open-redirect cases, a11y specs.

## Not in this feature
Application Insights sink/alerts, `/health`, request correlation ids, PITR test restore, blob lifecycle verification (all F30); MFA; WAF/Front Door; CSP nonce-based styles; penetration test by a third party; GDPR/PDPL tooling beyond unsubscribe/suppression.

## Verification / exit checklist
- [ ] Logs from a full local run contain no addresses/bodies/tokens (scan the `logs/` file with the same patterns the test uses).
- [ ] `docs/api-authorization-matrix.md` committed; inventory and cross-workspace tests green; the F19 super-admin 403 matrix still passes.
- [ ] CSP enforced with zero violations across every screen; headers present; `ForwardedHeaders` configured.
- [ ] `docs/security-review.md`, `docs/migrations-runbook.md`, `docs/perf-baseline.md`, `docs/ui-state-audit.md` committed; no High/Critical audit findings open.
- [ ] Perf budgets met (or each miss has a ticket and a measured plan); `TuneIndexes` migration reviewed.
- [ ] Expired session ⇒ clear message and return to the same page after sign-in.
- [ ] `dotnet test`, `ng test`, `dotnet build ZenLead.slnx` clean.
