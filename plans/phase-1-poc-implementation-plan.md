# ZenLead — Phase 1 (Proof of Concept) Implementation Plan
**Scope: Weeks 1–3 of [zen-lead-mvp-implementation-plan.md](zen-lead-mvp-implementation-plan.md) only.** Runs entirely locally, no Azure. Goal: prove register → add lead → generate AI draft works end-to-end, for ~$7–14 in OpenAI fees, before Gate 1 funds Phase 2.

---

## 0. Current state

*Updated 2026-10-07 after the gap remediation (see [phase-1-gap-remediation-plan.md](phase-1-gap-remediation-plan.md)).*

**Features 1–10 are implemented** on .NET 10 / Angular 21 (NgModule-based): domain + EF Core/Identity persistence, JWT auth with rotating refresh tokens, leads CRUD (manual `workspace_id` claim scoping), Semantic Kernel compose-email behind `IEmailComposer`, Angular auth/leads/lead-detail screens, usage logging, and the Gate 1 hardening pass. The remediation stages additionally made registration transactional, hardened the schema, mapped AI failures to friendly HTTP statuses, added input bounds and rate limits, persisted AI usage, made token refresh single-flight, and added fail-fast configuration plus a README.

**Still outstanding for Gate 1** (cannot be proven from code): the OpenAI dashboard hard cap, timed rehearsal runs and the screenshots/recording. Tracked in `docs/phase-1-gap-reanalysis.md`; the §5 checklist below is ticked only for items proven there.

### Prerequisites (resolved, before Milestone 1 starts)
- **Git** — repo will be `git init`'d and the current scaffolding committed before Milestone 1 coding begins (was an open question; resolved — init now, not deferred to Feature 4).
- **SQL Server LocalDB** — already installed and available; no setup task needed before Feature 1.
- **OpenAI API key/account** — **not yet set up.** Must be created, with billing and a hard $20 usage cap configured in the OpenAI dashboard, before Feature 5 (Milestone 2 kickoff). This is a standing to-do, not blocking Milestone 1.

---

## 1. Decisions to lock before coding

| Decision | Choice | Why |
|---|---|---|
| Local database | **SQL Server LocalDB** (not SQLite) | Phase 2 targets Azure SQL; staying on T-SQL/EF Core SQL Server provider now avoids a provider swap plus a migration rewrite in Sprint 1. |
| Identity model | **ASP.NET Core Identity**, `IdentityUser<Guid>` subclassed as `AppUser`, custom `Guid` key everywhere | Matches `WorkspaceId`/`LeadId` as `Guid` used later for multi-tenant filtering (Phase 2). |
| Workspace linkage in Phase 1 | One workspace per registering user, created transactionally at register time (`AppUser.WorkspaceId` FK) | `WorkspaceMember` + roles + invites are explicitly a **Sprint 1 (Phase 2)** item per the parent plan — don't build them early. |
| JWT signing | Symmetric key (HMAC-SHA256) from `dotnet user-secrets`, **not** `appsettings.json` | Key Vault is a Phase 2/Sprint 1 concern; user-secrets keeps the repo free of secrets in the meantime. |
| AI provider abstraction | `Microsoft.SemanticKernel` + `Microsoft.SemanticKernel.Connectors.OpenAI`, `IChatCompletionService` behind an `IEmailComposer` interface in `ZenLead.Application` | Matches parent plan's explicit mitigation: provider swap to Azure OpenAI later without touching call sites. |
| Angular UI kit | Angular Material, prebuilt **Azure/Blue** theme (`ng add @angular/material`) | Parent plan's `shared/` folder is described as holding "Angular Material theme, reusable components" — introduce it now so Milestone 1/2 screens aren't rebuilt in Phase 2. Blue is a safe default for a B2B tool; no custom branding decision needed yet. |
| API versioning | `/api/v1/...` from the first controller | Matches parent plan §6; cheap to do now, expensive to retrofit. |
| Token storage (Angular) | Access token held **in-memory only** (never persisted); refresh token in `localStorage` | Keeps the short-lived access token out of persistent storage (XSS-resistant); refresh token in `localStorage` avoids standing up cookie/CSRF handling in Phase 1 — accepted trade-off, revisit in Phase 2 hardening if needed. Pulls a real refresh-token flow into Milestone 1 (see Features 2/3 below) instead of deferring it. |

---

## 2. Milestone 1 — Scaffolding: auth, persistence, first migration

Goal: a user can register a workspace, log in, get a JWT, and a `Lead` row can be created and read back — all provable via Swagger/`.http` file, UI optional this milestone.

### Feature 1 — Domain & Persistence Foundation (branch: `feature/domain-entities-dbcontext`)
*Detailed file-by-file plan: [01-domain-entities-dbcontext.md](phase-1-features/01-domain-entities-dbcontext.md)*

No dependents yet; unblocks every other feature in this milestone.

- **PBI 1.1 — Domain entities & enums**
  - `ZenLead.Domain/Entities/`:
    - `Workspace` — `Id (Guid)`, `Name`, `CreatedAt`
    - `Lead` — `Id (Guid)`, `WorkspaceId`, `Name`, `Email`, `Title`, `Status (enum)`, `CreatedAt`
    - `RefreshToken` — `Id (Guid)`, `UserId (Guid)`, `TokenHash (string)`, `ExpiresAt`, `CreatedAt`, `RevokedAt (nullable)` — backs the in-memory-access-token / localStorage-refresh-token flow decided in §1
    - (User identity table comes from Identity in Infrastructure, not a hand-written Domain entity)
  - `ZenLead.Domain/Enums/LeadStatus.cs` — `New, Contacted, Replied, Unsubscribed` (full enum now, even though only `New` is reachable in Phase 1 — matches parent plan §5 without inventing a second migration later)
- **PBI 1.2 — EF Core DbContext skeleton**
  - Add NuGet to `ZenLead.Infrastructure`: `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore`
  - `ZenLead.Infrastructure/Persistence/ZenLeadDbContext.cs` extending `IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>`, with `DbSet<Workspace> Workspaces`, `DbSet<Lead> Leads`
  - `ZenLead.Infrastructure/Persistence/AppUser.cs` — `IdentityUser<Guid>` + `WorkspaceId (Guid)`, `DisplayName`

### Feature 2 — Auth & JWT Backend (branch: `feature/auth-jwt`)
*Detailed file-by-file plan: [02-auth-jwt.md](phase-1-features/02-auth-jwt.md)*

Depends on Feature 1 (`AppUser`, `ZenLeadDbContext`).

- **PBI 2.1 — Identity + JWT issuance wiring**
  - Add NuGet to `ZenLead.Api`: `Microsoft.AspNetCore.Authentication.JwtBearer`
  - Add NuGet to `ZenLead.Infrastructure`: `Microsoft.Extensions.Identity.Core` (pulled transitively, confirm)
  - `builder.Services.AddIdentityCore<AppUser>(...).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<ZenLeadDbContext>()` in `Program.cs`
  - `builder.Services.AddAuthentication(JwtBearerDefaults...).AddJwtBearer(...)` — validate issuer/audience/lifetime/signing key from config
  - `dotnet user-secrets init` on `ZenLead.Api`; set `Jwt:SigningKey`, `Jwt:Issuer`, `Jwt:Audience`, and `ConnectionStrings:Default` (LocalDB)
  - `ZenLead.Application/UseCases/Auth/` — `RegisterWorkspaceUseCase`, `LoginUseCase`, `RefreshTokenUseCase`, `IJwtTokenGenerator` and `IRefreshTokenService` interfaces; implementations in `ZenLead.Infrastructure`
  - Register flow: create `Workspace` → create `AppUser` with that `WorkspaceId` → issue a short-lived JWT (e.g. 15 min) with claims `sub`, `workspace_id`, `email` (the `workspace_id` claim is what Phase 2's EF global query filter will read — get the claim name right now) **plus** a long-lived refresh token (e.g. 7–14 days): generate a random value, store only its hash in the new `RefreshToken` table, return the raw value to the client once
  - `RefreshTokenUseCase`: given a raw refresh token, hash + look up, check `ExpiresAt`/`RevokedAt`, issue a new access token **and rotate** the refresh token (revoke the old row, insert a new one) — standard rotation so a stolen `localStorage` token has a short blast radius
- **PBI 2.2 — Auth & Lead controllers, first migration**
  - `ZenLead.Api/Controllers/V1/AuthController.cs` — `POST /api/v1/auth/register`, `POST /api/v1/auth/login`, `POST /api/v1/auth/refresh` (matches parent plan §6's API surface, just built in Phase 1 instead of assumed later)
  - `ZenLead.Api/Controllers/V1/LeadsController.cs` — `[Authorize]`, `GET /api/v1/leads`, `POST /api/v1/leads`, `GET /api/v1/leads/{id}` (filtered by `workspace_id` claim manually this milestone — the automatic EF global query filter is a Sprint 1 hardening item, not required for Gate 1)
  - DTOs + FluentValidation validators in `ZenLead.Application/Dtos` and `ZenLead.Application/Validation` (add `FluentValidation` + `FluentValidation.DependencyInjectionExtensions` NuGet to `ZenLead.Application`)
  - Delete `WeatherForecastController.cs` and `WeatherForecast.cs`
  - `dotnet ef migrations add InitialCreate -p ZenLead.Infrastructure -s ZenLead.Api`, apply to LocalDB, verify tables in SSMS/Azure Data Studio

### Feature 3 — Angular Auth Experience (branch: `feature/angular-auth`)
*Detailed file-by-file plan: [03-angular-auth.md](phase-1-features/03-angular-auth.md)*

Depends on Feature 2 (needs real `/auth/*` endpoints to call).

- **PBI 3.1 — Angular Material + auth core services**
  - `ng add @angular/material` — select the prebuilt **Azure/Blue** theme, typography + animations enabled (decided in §1)
  - `src/app/core/auth/auth.service.ts` — access token held in a private in-memory field only (never written to storage); refresh token persisted in `localStorage`; on service construction, if a refresh token exists, silently call `/auth/refresh` to re-populate the in-memory access token (covers page-reload)
  - `src/app/core/auth/auth.interceptor.ts` — attaches `Authorization: Bearer` header from the in-memory token; on a `401`, attempts one `/auth/refresh` + retry before giving up and redirecting to `/login`
  - `src/app/core/auth/auth.guard.ts`
- **PBI 3.2 — Register/Login screens & routing**
  - `src/app/features/auth/register/`, `src/app/features/auth/login/` components + Reactive Forms
  - Wire `app-routing-module.ts`: `/register`, `/login`, default redirect; guard placeholder route for `/leads`
  - Register `HTTP_INTERCEPTORS` and `provideHttpClient` in `app-module.ts`

### Feature 4 — Milestone 1 End-to-End Proof (branch: `feature/e2e-proof-milestone1`)
*Detailed file-by-file plan: [04-e2e-proof-milestone1.md](phase-1-features/04-e2e-proof-milestone1.md)*

Depends on Features 1–3 being merged.

- **PBI 4.1 — Manual e2e smoke pass**
  - Register via UI → land authenticated → call `POST /api/v1/leads` via a minimal leads screen (table only, no filters yet — filters are Sprint 2) → refresh the browser tab and confirm the silent `/auth/refresh` re-auths without bouncing to `/login` → `GET /api/v1/leads` still shows the lead
- **PBI 4.2 — Unit tests for auth use cases**
  - `ZenLead.Tests/Application/` — unit tests for `RegisterWorkspaceUseCase` (duplicate email, workspace creation), the JWT claim contents, and `RefreshTokenUseCase` (rotation, expired/revoked token rejection)

**Milestone 1 exit check:** register → login → create lead → list leads works via Swagger and via the UI, backed by a real LocalDB migration, and a browser refresh survives via silent token refresh rather than forcing re-login.

---

## 3. Milestone 2 — AI loop: Semantic Kernel + compose-email endpoint

Goal: `POST /api/ai/compose-email` returns a usable, personalised draft referencing the lead's real data, callable from a "Generate draft" button on the lead detail screen.

### Feature 5 — Semantic Kernel Wiring (branch: `feature/semantic-kernel-wiring`)
*Detailed file-by-file plan: [05-semantic-kernel-wiring.md](phase-1-features/05-semantic-kernel-wiring.md)*

Depends on Milestone 1 (`[Authorize]`-protected API must already exist).

- **PBI 5.1 — OpenAI account & spend cap (prerequisite)**
  - Create the OpenAI account/API key and set a **hard $20 usage cap in the OpenAI dashboard** (not just in-app) before anything else in this feature — matches the parent plan's Gate 1 spend ceiling. Do this before implementation starts, not mid-way, so account verification delays don't block the rest of the feature.
- **PBI 5.2 — Semantic Kernel DI wiring**
  - Add NuGet to `ZenLead.Infrastructure`: `Microsoft.SemanticKernel`, `Microsoft.SemanticKernel.Connectors.OpenAI`
  - `dotnet user-secrets set OpenAI:ApiKey ...` on `ZenLead.Api`
  - `ZenLead.Infrastructure/Ai/EmailComposer.cs` implementing `IEmailComposer` (interface lives in `ZenLead.Application` so controllers/use-cases never reference Semantic Kernel or OpenAI types directly)
  - `Kernel` registered via `AddKernel().AddOpenAIChatCompletion(modelId: "gpt-4o", apiKey: ...)` in DI

### Feature 6 — Compose-Email Use Case (branch: `feature/compose-email-use-case`)
*Detailed file-by-file plan: [06-compose-email-use-case.md](phase-1-features/06-compose-email-use-case.md)*

Depends on Feature 5 (`IEmailComposer`/`Kernel` registered).

- **PBI 6.1 — ComposeEmailUseCase**
  - `ZenLead.Application/UseCases/Ai/ComposeEmailUseCase.cs` — takes `LeadId`, loads `Lead` (+ `Company` if present, though `Company` entity itself is a Sprint 2 item — for Phase 1, a free-text "context" field on the request is enough, no need to build the full `Company` table early)
- **PBI 6.2 — Prompt design & structured output**
  - System prompt: personalised cold-outreach email, tone constraint, no fabricated claims about the sender's company, output subject + body as structured JSON (use Semantic Kernel's structured output / a simple `SubjectBodyDto` schema) so the API returns clean fields rather than a blob to regex apart
  - **Finding (Feature 6 implementation):** the model returns camelCase JSON (`"subject"`, `"body"`) but `SubjectBodyDto` has PascalCase properties, and `JsonSerializer.Deserialize` is case-sensitive by default — so a plain `Deserialize<SubjectBodyDto>(json)` yields null/empty fields (or throws) on real output. Deserialize with `new JsonSerializerOptions(JsonSerializerDefaults.Web)` (case-insensitive). Any test that parses model output must use the same options and a camelCase fixture, or it will pass against JSON the model never produces.
- **PBI 6.3 — Token usage logging guardrail**
  - Basic guardrail now (full `AiGenerationLog` table is Phase 2 §5, but log token usage to `ILogger` this milestone so the habit — and the data needed for the real table later — exists from the first call, per parent plan §9 "AI cost guardrails")

### Feature 7 — Compose Endpoint (branch: `feature/compose-endpoint`)
*Detailed file-by-file plan: [07-compose-endpoint.md](phase-1-features/07-compose-endpoint.md)*

Depends on Feature 6 (`ComposeEmailUseCase`).

- **PBI 7.1 — AiController compose-email endpoint**
  - `ZenLead.Api/Controllers/V1/AiController.cs` — `[Authorize]`, `POST /api/v1/ai/compose-email` `{ leadId, context? }` → `{ subject, body, tokensUsed }`
- **PBI 7.2 — Timeout/latency budget**
  - Gate 1 requires "under ~5 seconds" — set an explicit `HttpClient` timeout and surface a friendly error if OpenAI is slow, rather than letting the request hang
- **PBI 7.3 — Manual test requests**
  - `.http` file requests added to `ZenLead.Api.http` for quick manual testing without the UI

### Feature 8 — Angular Lead-Detail & Draft Generation UI (branch: `feature/angular-lead-detail`)
*Detailed file-by-file plan: [08-angular-lead-detail.md](phase-1-features/08-angular-lead-detail.md)*

Depends on Feature 7 (real compose endpoint to call).

- **PBI 8.1 — Lead-detail component**
  - `src/app/features/leads/lead-detail/` component: shows lead fields, "Generate draft" button, subject/body result panel (editable textareas, not persisted yet — sending is Sprint 3)
  - Route `/leads/:id` added, linked from the leads list row
- **PBI 8.2 — Loading/error states**
  - Loading state + error state (OpenAI timeout / rate limit) surfaced in the UI, not just the console

### Feature 9 — Cost Logging & AI Test Coverage (branch: `feature/cost-logging-polish`)
*Detailed file-by-file plan: [09-cost-logging-polish.md](phase-1-features/09-cost-logging-polish.md)*

Depends on Feature 6 (`ComposeEmailUseCase` + prompt logic to test).

- **PBI 9.1 — Unit tests with fake `IEmailComposer`**
  - `ZenLead.Tests` — unit test for the prompt-building logic (given a lead, assert required fields appear in the prompt) and for JSON-shape parsing of the model response, using a fake `IEmailComposer` so no real API calls happen in CI
- **PBI 9.2 — Cumulative spend tracking**
  - Track cumulative token spend locally (simple log scrape or a counter) to sanity-check against the ~$7–14 Phase 1 budget before Milestone 3

**Milestone 2 exit check:** from the lead-detail screen, clicking "Generate draft" returns a real, lead-specific GPT-4o draft in under 5 seconds, end to end through the Semantic Kernel abstraction.

---

## 4. Milestone 3 — Demo & Gate 1

Goal: no new functionality — stabilize what exists and rehearse the Gate 1 demo exactly as its exit criteria will be checked live.

### Feature 10 — Gate 1 Hardening & Demo Readiness (branch: `feature/gate1-hardening`)
*Detailed file-by-file plan: [10-gate1-hardening.md](phase-1-features/10-gate1-hardening.md)*

Depends on Milestones 1 and 2 being complete; this feature only touches existing behavior.

- **PBI 10.1 — Auth & UX hardening pass**
  - Fix auth edge cases: expired token handling on the frontend (redirect to `/login`, not a silent failure), duplicate-registration error messaging, empty-state UI for leads list
  - Confirm the manual workspace-scoping check in `LeadsController` actually rejects a second workspace's lead ID (write the regression test now even though the real global query filter is Sprint 1 — don't ship a demo with a visible cross-tenant leak)
- **PBI 10.2 — Clean-machine migration reproducibility**
  - Re-run `dotnet ef database update` from a clean LocalDB to confirm the migration is reproducible on a teammate's machine
- **PBI 10.3 — Reliability & timing pass**
  - Time the full register → add lead → generate draft loop 5–10 times back to back; if compose latency is inconsistent, add a simple retry-once-on-timeout in `EmailComposer`
  - Verify OpenAI spend to date against the ~$20 Gate 1 ceiling
- **PBI 10.4 — Gate 1 rehearsal**
  - Dry-run the exact Gate 1 checklist below on a fresh browser session / fresh LocalDB, end to end, without any manual DB seeding
  - Capture screenshots or a short screen recording as the artifact for the actual gate meeting
- **PBI 10.5 — Gate 1 demo & decision**
  - Live demo against the checklist; record the outcome (fund Phase 2 or not) and any follow-up fixes surfaced live for Sprint 1's backlog

---

## 5. Gate 1 exit criteria (unchanged from parent plan §3 — restated as a checklist)

- [ ] A user can register a workspace and log back in with a JWT-protected session
- [x] A lead can be created and persisted via EF Core *(proven live, see docs/phase-1-gap-reanalysis.md)*
- [ ] The AI compose endpoint returns a usable, personalised draft in under ~5 seconds
- [ ] Total spend to this point stays under ~$20 (OpenAI usage only)

---

## 6. Package additions by project (reference list)

| Project | New NuGet/npm packages |
|---|---|
| `ZenLead.Infrastructure` | `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Microsoft.SemanticKernel`, `Microsoft.SemanticKernel.Connectors.OpenAI` |
| `ZenLead.Api` | `Microsoft.AspNetCore.Authentication.JwtBearer` |
| `ZenLead.Application` | `FluentValidation`, `FluentValidation.DependencyInjectionExtensions` |
| `ZenLead.Client` | `@angular/material`, `@angular/cdk`, `@angular/animations` (via `ng add @angular/material`) |
| `ZenLead.Tests` | none new — xUnit already present; add `Moq` or `NSubstitute` if fakes get unwieldy |

Note: refresh-token hashing uses `System.Security.Cryptography` (BCL, no package needed).

---

## 7. Explicitly out of scope for Phase 1 (don't build yet)

Carried forward from the parent plan so Milestone 1–3 work doesn't quietly absorb Phase 2 scope:
- `WorkspaceMember`, roles, invite flow
- `Company` entity, CSV import
- Campaigns, sequencing, SendGrid sending
- Inbox, reply classification
- Analytics dashboards
- Azure provisioning, CI/CD, Key Vault, Application Insights
- EF Core global query filter for tenant isolation (manual claim check is enough for Gate 1; the filter is a named Sprint 1 hardening task in the parent plan's risk table)

---

*Scoped to Proposal Phase 1 only, weeks 1–3 of [zen-lead-mvp-implementation-plan.md](zen-lead-mvp-implementation-plan.md). Local-only, no Azure spend.*
