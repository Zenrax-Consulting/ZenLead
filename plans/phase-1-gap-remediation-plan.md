# ZenLead Phase 1: Gap Remediation Plan

**Source:** [phase-1-gap-analysis.md](../docs/phase-1-gap-analysis.md) (2026-10-07)
**Goal:** Close the High, Medium and Low gaps before Gate 1, then re-run the gap analysis to confirm nothing is left and nothing new was introduced.
**Scope rule:** Phase 1 only. Per [phase-1-poc-implementation-plan.md](phase-1-poc-implementation-plan.md) §7, do not pull in WorkspaceMember, the EF global query filter, Azure, or other Phase 2 items.

Work is split into feature branches, each merged by PR (the repo's existing workflow). Every branch ends with its own verification step. Stage 6 is the full re-analysis.

## Order of work

| Stage | Branch | Closes | Depends on |
|---|---|---|---|
| 1 | `fix/registration-and-schema` | H1, M1 | none |
| 2 | `fix/ai-error-handling-and-limits` | M2, M3, M4 | none |
| 3 | `fix/angular-auth-flow` | M5, M6, L1, L2, L3 | none |
| 4 | `fix/config-and-hygiene` | L4, L5, L6, L8, L9 | Stage 1 (migration) |
| 5 | `docs/sync-plan-and-claude-md` | L7 | Stages 1–4 |
| 6 | `verify/gate1-gap-reanalysis` | H2, plus re-check of everything | Stages 1–5 |

Stages 1–3 are independent and can run in parallel. Stage 4 waits for Stage 1 so there is a single schema migration sequence.

---

## Stage 1: Registration and schema (H1, M1)

### 1.1 Transactional registration (H1)
- Add an `IUnitOfWork` (or `IRegistrationTransaction`) abstraction in `ZenLead.Application/Abstractions`, implemented in Infrastructure over `ZenLeadDbContext.Database.BeginTransactionAsync`.
- In `RegisterWorkspaceUseCase`, wrap `workspaces.CreateAsync` and `identity.CreateUserAsync` in one transaction. Roll back on any failure. Issue the tokens after commit.
- Change `WorkspaceRepository.CreateAsync` and `IdentityService.CreateUserAsync` so they don't each commit independently inside the transaction scope.
- Align password rules: either relax Identity (`RequireDigit`, `RequireUppercase`, `RequireNonAlphanumeric`) or extend `RegisterRequestValidator` to mirror Identity's options. Choose one source of truth (recommended: set the Identity options explicitly in `Program.cs` and mirror them in the validator).
- Map Identity failures to a 400 validation problem, not a 409. Keep 409 for duplicate email only.

**Tests (xUnit, new):**
- `RegisterWorkspaceUseCaseTests`: user creation throws, so no workspace remains (use a fake unit of work that records commit / rollback).
- Password accepted by the validator is never rejected by Identity (table-driven over a few passwords).
- Duplicate email returns 409 and creates no workspace.

### 1.2 Schema hardening (M1)
Add `ZenLead.Infrastructure/Persistence/Configurations/` with `IEntityTypeConfiguration` classes, applied in `OnModelCreating`:
- `Lead`: `Name` max 200, `Email` max 256, `Title` max 200; FK `WorkspaceId` to `Workspaces` (restrict delete); index on `WorkspaceId`.
- `RefreshToken`: `TokenHash` max 64 (a SHA-256 hash in Base64 is 44 chars); unique index on `TokenHash`; FK `UserId` to `AspNetUsers` (cascade delete); index on `UserId`.
- `Workspace`: `Name` max 200.
- `AppUser`: `DisplayName` max 200; FK `WorkspaceId` to `Workspaces` (restrict delete); index on `WorkspaceId`.
- Generate one migration, `HardenSchema`. Because the data is local-only and the schema is Phase 1, check that the column narrowing and new FKs apply cleanly to a database that already contains rows (orphan cleanup script only if needed).

**Verification:**
- `dotnet ef migrations add HardenSchema` produces only the intended changes (review the diff).
- `dotnet ef database update` succeeds on (a) the existing LocalDB and (b) a dropped, fresh LocalDB.
- Inspect the schema in SSMS or Azure Data Studio for the FKs and indexes above.
- Existing tests plus the new tests pass.

---

## Stage 2: AI error handling, limits, usage tracking (M2, M3, M4)

### 2.1 Failure mapping (M2)
- In `EmailComposer`, catch `HttpOperationException` and map to Application-level exceptions, so Semantic Kernel types never reach Api (per the layering rule). Add to `ZenLead.Application/Abstractions`: `AiProviderException` with a `Kind` of `RateLimited`, `Unavailable`, or `InvalidResponse`.
- Guard `response.Content` for null or empty, and wrap JSON deserialization failure as `InvalidResponse`.
- `AiController` (or an exception filter): `RateLimited` becomes 429, `Unavailable` and `Timeout` become 503/504, `InvalidResponse` becomes 502. Use ProblemDetails with a friendly `message`.
- `lead-detail.ts`: show tailored messages for 429, 502, 503 and 504. Keep a generic fallback.

### 2.2 Input bounds and per-user throttle (M3)
- Add `ComposeEmailRequestValidator` (FluentValidation): `LeadId` not empty, `Context` max 1000 chars. Register it and call it from `AiController`, like the other controllers.
- Add ASP.NET Core rate limiting (`AddRateLimiter`, fixed window partitioned by `workspace_id`, e.g. 10 requests / minute) on `compose-email`. Return 429 with `Retry-After`.
- Cap `max_tokens` in `OpenAIPromptExecutionSettings` so cost per call has an upper bound.

### 2.3 Usage tracking (M4)
- Replace the static `TokenUsageTracker` with an injected `ITokenUsageTracker` that persists. Phase 1 choice: append one line per call (timestamp, workspace, model, prompt tokens, completion tokens) to a local log file or a small `AiUsageLog` table. If a table is chosen, add it to the same Stage 1 migration sequence (and note it's a lightweight forerunner of the Phase 2 `AiGenerationLog`).
- Record estimated dollar cost using configured per-token prices (`OpenAI:PricePer1KInput/Output` in config).
- `GET /api/v1/ai/token-usage` returns only the caller's workspace totals. Do not return a global total.
- Tests: the tracker survives a new instance (persistence), the endpoint is scoped, and cost math is correct.

**Verification:**
- New and updated unit tests with a fake `IChatCompletionService` throwing each failure type, asserting the correct HTTP status.
- Controller tests for validator rejection (oversize `Context`) and for the rate limit.
- Manual: with a bad API key, 429 and 5xx simulations (use a stub handler) show friendly UI messages.

---

## Stage 3: Angular auth flow and UX (M5, M6, L1, L2, L3)

- **M5, single-flight refresh:** in `AuthInterceptor`, share one in-flight refresh (`shareReplay` on a stored observable, cleared on completion). Parallel 401s wait on the same refresh, then each retries once. Logout only if that single refresh fails.
- **M5, backend:** make `ValidateAndRotateAsync` safe under concurrency. Revoke with a conditional update (`WHERE RevokedAt IS NULL`) and check the affected-row count, or use a `rowversion` concurrency token on `RefreshToken`. A losing request returns "not found/revoked" cleanly. If the rowversion column is chosen, fold it into the Stage 1 migration.
- **M6:** after successful `register`, navigate to `/leads` (the session is already stored). Drop the detour through `/login`.
- **L1:** surface the server message. 409 shows "That email is already registered." 400 shows field errors from the ProblemDetails `errors`. Other statuses show a generic retry message.
- **L2:** clear `errorMessage` at the start of `refresh()` and `submit()` in `LeadsList`.
- **L3:** add a minimal toolbar in `app.html` (shown when authenticated): app name, link to Leads, **Logout** button that calls `AuthService.logout()` and routes to `/login`. Add a "Back to leads" link on lead detail. This is a navigation shell only, so no new product scope.

**Tests (Vitest):**
- Interceptor: three concurrent 401s trigger exactly one `/auth/refresh` call and all three requests are retried.
- Interceptor: refresh failure logs out and redirects once.
- Register: success navigates to `/leads`; a 409 shows the duplicate-email message.
- Backend: two simultaneous refreshes of the same token, so exactly one succeeds (service-level test against a real in-memory or LocalDB provider; a pure fake can't prove this).

**Verification:** `ng test` and `ng build` pass. Manual: expire the access token (shorten the lifetime temporarily), open a page that fires parallel requests, and confirm no logout.

---

## Stage 4: Config and hygiene (L4, L5, L6, L8, L9)

- **L4, fail-fast config:** bind `Jwt` and `OpenAI` to options classes validated on start (`ValidateOnStart`). Fail with a clear message naming the missing key. Enforce a minimum signing-key length of 32 bytes. Add a "Local setup" section to `README.md` (new, repo root) listing the five user-secrets commands and the `dotnet ef database update` step.
- **L5, Swagger UI:** in Development only, add a UI over the existing OpenAPI document (e.g. Scalar or Swashbuckle UI) with bearer-auth support. Alternatively, record in the plan that `.http` is the accepted proof route. Pick one and note it.
- **L6, frontend hygiene:** remove `jest-editor-support` from `package.json`, delete the stale `karma.conf.js`, run `npm audit` and upgrade what can be upgraded without breaking Angular 21. Document any remaining accepted advisories.
- **L8, backend minor:**
  - Replace `Guid.Parse(User.FindFirstValue(...)!)` with a shared `ICurrentUser` helper (or controller base method) that returns 401 when the claim is missing or malformed.
  - Add expired / revoked refresh-token cleanup (a delete on each issue for that user's stale rows is enough for Phase 1).
  - Login throttling: reuse the rate limiter from 2.2 on `auth/login` and `auth/register`. Identity lockout is optional, so decide and record.
- **L9, test gaps:** add a `RefreshTokenUseCase` test (success, rejected token returns null, user missing). Add Angular component tests for Login (success / failure) and LeadDetail (loading / 504 / 429 / done). Keep these targeted, per the testing conventions.

**Verification:** start the API with each secret missing in turn and confirm a clear startup error. `npm audit` summary captured. All tests pass.

---

## Stage 5: Documentation sync (L7)

- Rewrite `CLAUDE.md` "Project state" to match reality (Phase 1 built, what is still out of scope). Fix the `proxy.conf.js` description (`/api` is proxied).
- Update `phase-1-poc-implementation-plan.md` §0 "Current state", and tick §5 only after Stage 6 proves each item.
- Add a short note on decisions made during remediation (transaction approach, password-rule source of truth, usage-tracker storage, rate-limit values).

---

## Stage 6: Gap re-analysis (verification gate)

Run after Stages 1–5 are merged to `master`. The aim is to prove the remaining gaps are closed and to catch any gaps introduced by the fixes. Output: `docs/phase-1-gap-reanalysis.md`, in the same five-category format as the original report.

### 6.1 Automated checks
- [ ] `dotnet build ZenLead.slnx` has 0 errors and no new warnings from our code.
- [ ] `dotnet test ZenLead.Tests` all green. Record the new total (was 21).
- [ ] `ng test --watch=false` all green (was 9). `ng build` succeeds.
- [ ] `npm audit` and `dotnet list package --vulnerable` summaries recorded.

### 6.2 Fresh-environment check (closes H2 evidence, PBI 10.2 and 10.4)
- [ ] Drop the LocalDB database. Run `dotnet ef database update` from scratch with no manual seeding. Confirm every migration applies and the schema matches Stage 1.
- [ ] On a clean clone (or a second machine / clean user profile), follow only the README to get to a running app. Note any step the README missed.
- [ ] Start with each required secret removed (one at a time) and confirm the clear fail-fast error.

### 6.3 Gate 1 rehearsal (closes H2)
Run the full loop on a fresh browser session. Capture a short recording or screenshots into `docs/gate1-evidence/`.
- [ ] Register a workspace, then log out and log back in with a JWT-protected session.
- [ ] Create a lead and confirm it persists after a browser refresh (silent refresh, no bounce to `/login`).
- [ ] Generate a draft. Time the full register, add lead, generate loop **5–10 times** and record each compose latency in a table. Pass criterion: typically under ~5 s. If it isn't, record the p50 / max and decide explicitly whether to lower the timeout or accept the retry (and update the plan).
- [ ] Confirm the OpenAI dashboard shows the **$20 hard cap** (screenshot) and current spend under ~$20. Compare against the new usage log to check the cost estimate is in the right range.

### 6.4 Targeted regression probes (things the fixes could have broken)
| Probe | Expected |
|---|---|
| Register with a password that previously orphaned a workspace | 400, no new `Workspaces` row |
| Register with an existing email | 409, no new `Workspaces` row |
| Cross-tenant: workspace B user requests workspace A lead on `GET /leads/{id}` and `POST /ai/compose-email` | 404 on both |
| Token with a missing `workspace_id` claim | 401, not 500 |
| 3 parallel requests with an expired access token | One refresh call, all succeed, no logout |
| Replay a rotated (old) refresh token | 401 |
| `compose-email` with `Context` over 1000 chars | 400 |
| 11 compose calls in a minute from one workspace | 11th returns 429 with `Retry-After` |
| `GET /ai/token-usage` as workspace B | Only B's totals |
| Bad OpenAI key / stub 429 / stub 5xx / stub malformed JSON | Friendly UI message per case, no 500 |
| Existing `InitialCreate`-era data after `HardenSchema` | Migration applies without data loss |

### 6.5 Re-run the gap analysis
Repeat the original method against `phase-1-poc-implementation-plan.md` (Features 1–10 and §5, §7), now also checking:
- [ ] Every gap ID (H1–H2, M1–M6, L1–L9) is marked **Closed**, **Partially closed**, or **Accepted** with a one-line evidence reference (test name, file, or screenshot).
- [ ] **New gaps:** review the diff of Stages 1–5 for scope creep into §7 out-of-scope items, layering violations (Semantic Kernel / OpenAI types outside Infrastructure; Api to Domain direct references), missing `/api/v1` prefixes, new endpoints without `[Authorize]` or tenant scoping, new secrets in tracked files, and new config keys not in the README.
- [ ] Re-grade anything new into Critical / High / Medium / Low / No Gap.
- [ ] Tick the §5 Gate 1 checklist in the plan only for items proven in 6.3.

### 6.6 Exit criteria
- No Critical or High items open.
- Any Medium or Low items left open are listed as **Accepted** with a reason, or moved to the Sprint 1 backlog.
- `docs/phase-1-gap-reanalysis.md` and `docs/gate1-evidence/` are committed.
- Plan §0 and §5, plus `CLAUDE.md`, reflect the final state.

---

## Risks and notes
- **Migration ordering:** Stages 1, 2.3 (if a table is chosen) and 3 (if rowversion is chosen) all touch the schema. Land them as one `HardenSchema` migration, or sequence the PRs strictly, to avoid conflicting model snapshots.
- **Transaction scope with Identity:** `UserManager` calls `SaveChanges` internally. Verify they participate in the ambient `DbContext` transaction (same scoped context) rather than assuming it.
- **Rate-limit values** are placeholders. Tune them after the 6.3 timing runs so the demo can't trip its own limit.
- **Latency vs the 5 s criterion** (H2) may need a product decision, not a code change. Surface it in 6.3 rather than silently changing the timeout.
