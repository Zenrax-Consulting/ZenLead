# ZenLead Phase 1 (PoC): Gap Analysis

**Compared against:** `plans/phase-1-poc-implementation-plan.md` (Features 1–10, Gate 1 exit criteria, out-of-scope list)
**Date:** 2026-10-07 · **Branch:** `master` @ `f34e14a`

**Verification run:** `dotnet build ZenLead.slnx` passes (0 errors). `dotnet test ZenLead.Tests` passes (21/21). Angular `ng test` passes (9/9, 3 files). The app was not run against a live OpenAI key or a fresh LocalDB, so runtime items are marked *unverified*.

## Summary

Phase 1 is functionally complete. Every PBI in Features 1–9 has working code, and Feature 10's code items are present. No Critical gaps were found. The gaps are mostly hardening and consistency problems, plus several Gate 1 verification steps that code can't prove.

| Category | Count |
|---|---|
| Critical | 0 |
| High | 2 |
| Medium | 6 |
| Low | 9 |
| No Gap | 23 areas (listed at the end) |

---

## Critical

None. Nothing blocks the Gate 1 checklist (register/login, persist lead, compose draft, spend cap) on the evidence in the code.

---

## High

### H1. Registration is not transactional (plan §2 Feature 2 / PBI 2.1)
- **Plan:** "Workspace … created transactionally at register time."
- **Code:** `RegisterWorkspaceUseCase` calls `workspaces.CreateAsync` (which saves immediately), then `identity.CreateUserAsync`. There is no transaction around the two calls.
- **Impact:** If user creation fails, an orphan `Workspace` row is left behind. This happens on a duplicate-email race, or when Identity rejects a password the validator accepted. The validator only checks `MinimumLength(8)`, while Identity's defaults also require a digit, an upper-case letter, a lower-case letter and a symbol. A password like `password1` passes validation, fails in Identity, leaves an orphan workspace, and the API returns a 409 "Conflict" carrying Identity's raw message.
- **Fix:** Wrap in a transaction (or `IUnitOfWork`), and align the validator with Identity's password options (or relax Identity's options).

### H2. Gate 1 verification steps cannot be confirmed from the repo (PBI 5.1, 10.2–10.5, §5)
- All four Gate 1 checkboxes in plan §5 are still unticked. No rehearsal evidence is stored (screenshots, recording, timing log).
- **Unverified:** the OpenAI **$20 hard cap** (PBI 5.1, an external dashboard setting), the **<5 s compose latency** (nothing records timings), a clean-LocalDB `ef database update` run, and the 5–10 back-to-back timing pass.
- The 5 s target also conflicts with the implementation. The `HttpClient` timeout is 10 s with one retry, so a slow call can take about 20 s before the 504.
- **Fix:** Run the rehearsal, record results in `docs/`, and tick the plan checklist. Decide whether the 10 s timeout and retry are acceptable against the 5 s criterion.

---

## Medium

### M1. Database schema lacks keys and indexes (Feature 1)
`InitialCreate` defines no foreign keys and no indexes beyond Identity's.
- `Leads.WorkspaceId`, `AspNetUsers.WorkspaceId` and `RefreshTokens.UserId` have no FK to `Workspaces` or `AspNetUsers`.
- `Leads.WorkspaceId` has no index. Every list and get filters on it.
- `RefreshTokens.TokenHash` is `nvarchar(max)`, so it can't be indexed. Each refresh does a full table scan.
- All string columns are `nvarchar(max)`, while the validators cap `Name` at 200 and `Email` at 256.
- `OnModelCreating` has no entity configuration at all.
- **Impact:** Orphan rows are possible, and queries will degrade as data grows. Fixing it later means a second migration.

### M2. AI error handling covers timeouts only (PBI 7.2, 8.2)
- The plan asks for friendly errors on "OpenAI timeout / rate limit".
- `AiController` maps only `OperationCanceledException` to 504. A 429, a 5xx, an invalid key, or malformed model JSON (`InvalidOperationException`) becomes an unhandled 500. The UI then shows the generic "Failed to generate a draft."
- A null `response.Content` also throws a `NullReferenceException` at `EmailComposer.cs:47`.

### M3. No input bounds on the AI endpoint (PBI 7.1, §9 cost guardrails)
- `ComposeEmailApiRequest.Context` has no length limit and no validator. `AiController` is the only controller with no FluentValidation.
- Lead fields and `Context` go straight into the prompt, so a large `Context` inflates token cost.
- There is no per-user or per-workspace rate limit.
- The $20 cap exists only if the dashboard setting was done (see H2).

### M4. Token tracking is process-local, and its endpoint is unscoped (PBI 6.3, 9.2)
- `TokenUsageTracker` is a static counter that resets on every restart, so it can't track cumulative spend toward the ~$7–14 budget.
- It records tokens, not dollars.
- `GET /api/v1/ai/token-usage` is `[Authorize]` but returns the global total, so any tenant can see aggregate usage.
- The plan allows "a counter", but this one doesn't meet the "cumulative" intent. The plan itself calls it only a rough proxy.

### M5. Concurrent 401s break the refresh flow (PBI 3.1)
- `AuthInterceptor` has no single-flight lock. When several requests hit 401 together (for example on an expired access token), each calls `/auth/refresh` with the same refresh token.
- Rotation revokes the token on first use, so the other calls fail. The failing call then runs `logout()` and redirects to `/login`.
- Two or three parallel calls on one page can force a spurious logout.
- `RefreshTokenService` also has no concurrency protection. Two simultaneous refreshes can both pass the "not revoked" check.

### M6. Post-registration flow differs from the plan (PBI 4.1)
- Plan: "Register via UI → land authenticated."
- Code: `AuthService.register` stores the session, but `Register` navigates to `/login`. The user is already authenticated and still sees a login form.
- It works, but it's a demo-visible detour.

---

## Low

### L1. Registration error message is generic (PBI 10.1)
`Register` shows "Registration failed. Email may already be in use." for every failure, including validation errors and 5xx. The plan marks duplicate-email messaging as covered. It is, but only for one case, and the backend's 409 message is never used.

### L2. Stale error banner on leads list
`LeadsList.errorMessage` is never cleared on a successful refresh or submit. A past failure leaves the banner up and suppresses the empty state.

### L3. No logout control or navigation shell
`app.html` is only `<router-outlet>`. `AuthService.logout()` has no UI trigger. This is not in the plan, but it makes the demo awkward.

### L4. JWT / config fail-fast
`Program.cs` uses `jwtSection["SigningKey"]!` and `OpenAI:ApiKey!`. A missing secret on a new machine produces a null-reference or argument exception at startup, not a clear message. The HMAC-SHA256 key length isn't validated. There is also no setup documentation listing the five required user-secrets keys, which hurts the clean-machine goal in PBI 10.2.

### L5. No Swagger UI
The plan says endpoints are provable "via Swagger/`.http`". Only the OpenAPI document (`MapOpenApi`) is mapped, and there is no UI. The `.http` file covers every endpoint, so the impact is low.

### L6. Frontend dependency and tooling hygiene
- `package.json` lists `jest-editor-support: "*"` as a runtime dependency, which is stray.
- `karma.conf.js` is stale (the runner is Vitest).
- The build reports vulnerable transitive npm packages (for example `undici`, `proxy-addr`, `source-map-js`), 25 warnings in total.

### L7. Plan and CLAUDE.md are stale
- Plan §0 still says "no Identity, no EF Core … no Angular screens yet", and the §5 checklist is unticked.
- `CLAUDE.md` still describes the repo as a scaffold with only `WeatherForecastController`, and says `proxy.conf.js` proxies only `/weatherforecast`. The file now proxies `/api`.
- This will mislead future sessions.

### L8. Minor backend gaps
- Refresh tokens are never purged after expiry or revocation.
- Login has no lockout or throttling.
- `LeadsController.GetWorkspaceId` and `AiController` both call `Guid.Parse(...!)`. A token missing the claim gives a 500, not a 401.
- `Lead.Email` is not unique per workspace (not required by the plan).
- Lead detail has no "back to leads" link.

### L9. Test coverage notes
- There is no `RefreshTokenUseCase`-level test. Rotation is covered at the service level, which satisfies the intent.
- There are no Angular tests for the login, register, or lead-detail components. The plan asks only for targeted tests, so this is acceptable.

---

## No Gap (implemented as planned)

| Plan item | Evidence |
|---|---|
| Solution layering, project references, .NET 10 | `ZenLead.slnx`, csproj files; build passes |
| Domain entities `Workspace`, `Lead`, `RefreshToken`; full `LeadStatus` enum | `ZenLead.Domain/` |
| `AppUser : IdentityUser<Guid>` with `WorkspaceId`, `DisplayName` | `ZenLead.Infrastructure/Persistence/` |
| `ZenLeadDbContext` on `IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>`, SQL Server LocalDB | `Program.cs`, `ZenLeadDbContext.cs` |
| `InitialCreate` migration exists | `Migrations/20261006173953_InitialCreate.cs` (quality gaps in M1) |
| Identity wiring, `AddRoles`, JWT bearer validating issuer, audience, lifetime and key | `Program.cs` |
| Secrets via user-secrets, not `appsettings.json` | `appsettings*.json` clean; all 5 keys present in user-secrets |
| JWT claims `sub`, `workspace_id`, `email`; 15-minute lifetime | `JwtTokenGenerator.cs` + test |
| Refresh token: random, SHA-256 hashed, 14-day, rotated on use, revoked / expired rejected | `RefreshTokenService.cs` + tests |
| `Register` / `Login` / `Refresh` use cases and interfaces in Application | `UseCases/Auth/`, `Abstractions/` |
| Routes under `/api/v1/` | `AuthController`, `LeadsController`, `AiController` |
| FluentValidation validators in Application | `Validation/` |
| `WeatherForecast` sample removed | Not present |
| Manual workspace check in `LeadsController`, 404 for foreign leads, plus regression test | `LeadsController.cs`, `LeadsControllerTests` |
| Compose endpoint also workspace-scoped | `ComposeEmailUseCase.cs`, `AiControllerTests` |
| Angular Material (Azure/Blue theme) | `material-theme.scss`, `app-module.ts` |
| In-memory access token, `localStorage` refresh token, silent restore on startup | `auth.service.ts`, `provideAppInitializer` |
| Interceptor: bearer header, 401 → refresh → retry → redirect to `/login` | `auth.interceptor.ts` + spec |
| Auth guard, `/leads` and `/leads/:id` routes | `auth.guard.ts`, `app-routing-module.ts` |
| Register / Login reactive forms; NgModule-based Angular | `features/auth/` |
| Leads list with add-lead form and empty state | `leads-list.*` |
| `IEmailComposer` in Application, Semantic Kernel only in Infrastructure | `Abstractions/IEmailComposer.cs`, `EmailComposer.cs` |
| `AddKernel().AddOpenAIChatCompletion("gpt-4o")` | `Program.cs` |
| Structured JSON output; camelCase parsing finding applied (`JsonSerializerDefaults.Web`) | `EmailComposer.cs` + `SubjectBodyDtoParsingTests` |
| Prompt rules (no fabricated claims, tone, length) and prompt-content test | `EmailComposer.cs`, `EmailComposerPromptTests` |
| Token usage logged to `ILogger` | `EmailComposer.cs` |
| Retry once on timeout, with retry-count tests | `EmailComposer.cs`, `EmailComposerRetryTests` |
| 504 with friendly message on timeout; UI loading / error states | `AiController.cs`, `lead-detail.*` |
| Lead-detail: Generate draft, editable subject / body, route linked from list | `lead-detail.*`, `leads-list.html` |
| `.http` requests for every endpoint | `ZenLead.Api.http` |
| Fake `IEmailComposer` used in tests (no real OpenAI calls) | `Tests/Application/Ai/Fakes.cs` |
| Out-of-scope items not built early (WorkspaceMember, Company, campaigns, global filter, Azure) | None present |
| `/api` proxy path registered for `ng serve` | `proxy.conf.js` |
| Build and tests green | 21 backend and 9 frontend tests pass |

---

## Recommended order of work

1. **H2** — run the Gate 1 rehearsal and record the evidence. Confirm the OpenAI cap.
2. **H1, M1** — fix transactional registration and add the schema keys and indexes in one follow-up migration. Do it before Phase 2 builds on this schema.
3. **M2, M3** — map more AI failure modes to friendly errors, and bound `Context` length.
4. **M5, M6** — single-flight refresh and the post-register redirect.
5. **L7** — update `CLAUDE.md` and plan §0 / §5. Do the remaining Low items opportunistically.
