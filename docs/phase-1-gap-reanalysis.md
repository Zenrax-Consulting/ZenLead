# ZenLead Phase 1: Gap Re-analysis (after remediation)

**Compared against:** `plans/phase-1-poc-implementation-plan.md` and `plans/phase-1-gap-remediation-plan.md` (Stage 6)
**Date:** 2026-10-07 · **Baseline:** [phase-1-gap-analysis.md](phase-1-gap-analysis.md)
**Evidence:** [gate1-evidence/rehearsal-2026-10-07.md](gate1-evidence/rehearsal-2026-10-07.md)

**Automated checks:** `dotnet build` 0 errors · backend tests 64/64 (was 21) · Angular tests 27/27 (was 9), `ng build` OK · no vulnerable NuGet packages reported · npm advisories 12 → 4 (accepted, below) · fresh-LocalDB migration clean · layering review clean (Semantic Kernel types only in Infrastructure and the `Program.cs` composition root; only `AuthController` lacks `[Authorize]`, by design; all routes under `/api/v1`; no secrets in tracked files).

## Result

No Critical gaps. **One High item remains open (H2), and it needs a person, not code:** setting the OpenAI limit to $20 and a browser rehearsal recording. Everything else from the original report is closed or explicitly accepted. Remediation exit criterion "no High open" is therefore **not yet met**.

| Original gap | Status | Evidence |
|---|---|---|
| **H1** Registration not transactional | **Closed** | `RegisterWorkspaceUseCaseTests` (rollback, duplicate), `RegisterRequestValidatorTests`; live probe: weak password → 400 with 0 workspaces, duplicate → 409 |
| **H2** Gate 1 steps unverifiable | **Partially closed** | Done: fresh-DB migration, 5 timed runs, spend estimate, regression probes. Done: OpenAI dashboard checked (spend $0.02, balance $14.98). Open: dashboard limit is **$100, not the planned $20 hard cap**; browser rehearsal recording; second-machine README run. Latency: median 2.3 s, but cold first call 5.2 s (see N3) |
| **M1** Schema lacks keys/indexes | **Closed** | `HardenSchema` applied to fresh and existing DB; FKs confirmed in `sys.foreign_keys` |
| **M2** AI error handling | **Closed** | `EmailComposerFailureTests`, `AiControllerTests` (429/502/503/504); UI messages in `LeadDetail` spec. Not exercised against the live provider |
| **M3** No input bounds | **Closed** | Validator + `max_tokens` cap; live: oversize `Context` → 400, 429 after 10/min per workspace |
| **M4** Token tracking | **Closed** | `AiUsageLogs` populated (5 rows live), survives restart test, endpoint workspace-scoped |
| **M5** Concurrent 401 / rotation race | **Closed** | Interceptor specs (3 parallel 401s → 1 refresh); `ConcurrentUseOfSameToken_ExactlyOneSucceeds` on SQLite; live 4 simultaneous refreshes → exactly one 200. Browser behaviour not observed |
| **M6** Register → `/login` detour | **Closed (code)** | `register.ts` navigates to `/leads`; not covered by a navigation unit test or browser check |
| **L1** Generic register error | **Closed** | `Register.messageFor` spec |
| **L2** Stale error banner | **Closed (code)** | `refresh()`/`submit()` clear `errorMessage`; no dedicated test |
| **L3** No logout / navigation | **Closed** | `app.spec.ts` toolbar + logout; back link on lead detail |
| **L4** Config fail-fast / setup docs | **Closed** | `StartupConfigurationTests`; live start with bad secrets shows named errors; `README.md` |
| **L5** No Swagger UI | **Closed** | Scalar at `/scalar` (Development), bearer scheme in OpenAPI doc |
| **L6** Frontend hygiene | **Partially closed, remainder Accepted** | Stray dependency and karma config removed. 4 advisories remain (`@angular/router` SSR DoS, `piscina`, `undici`): fixes need breaking upgrades; SSR unused, others are build tooling |
| **L7** Stale CLAUDE.md / plan | **Closed** | `CLAUDE.md` and plan §0 updated; remediation decisions recorded |
| **L8** Minor backend gaps | **Closed; lockout Accepted** | Missing claim → 401 (tests), stale refresh tokens cleaned (test), auth throttled per IP (live). Identity lockout deliberately not enabled |
| **L9** Test gaps | **Closed** | `RefreshTokenUseCaseTests`, Login and LeadDetail specs |

## New gaps found in the remediation diff

All Low; none introduced Critical/High/Medium risk.

| ID | Gap | Notes |
|---|---|---|
| N1 | Auth rate limit keys on `RemoteIpAddress` | Behind a proxy (Azure App Service, or the `ng serve` proxy) every client can share one bucket (20/min). Add forwarded-headers handling in Phase 2. Rapid scripted registrations can hit it, which matters for demo scripts |
| N2 | AI spend undercounted on failed calls | Usage is recorded only after a usable draft. A call that consumed tokens but returned invalid JSON is not logged. The OpenAI dashboard stays authoritative |
| N3 | Cold-start latency | First compose after process start took 5.2 s (vs ~2.3 s warm); HTTP timeout is still 10 s with one retry (worst case ~20 s before a 504). Consider a warm-up call or a decision to accept for the demo |
| N4 | Transaction vs retrying execution strategy | `EfUnitOfWork` uses a plain transaction. If `EnableRetryOnFailure` is turned on for Azure SQL in Phase 2, it must move into `CreateExecutionStrategy().ExecuteAsync` |
| N5 | Compose rate limit counts rejected (400) requests | Intentional and harmless; noted so tuning accounts for it |
| N6 | Browser-level behaviour unverified | Silent refresh on reload, empty state, toolbar and parallel-401 handling are covered by unit tests but not observed in a browser |

## Gate 1 checklist (plan §5)

- [ ] Register a workspace and log back in with a JWT-protected session. *API register, refresh and protected calls proven live; UI login and browser session not rehearsed.*
- [x] A lead can be created and persisted via EF Core. *Proven live against a fresh database.*
- [ ] AI compose returns a usable, personalised draft in under ~5 s. *4 of 5 runs 2.1–2.6 s; first (cold) run 5.2 s. Left unticked pending a decision on N3.*
- [x] Total spend under ~$20. *Dashboard (screenshot): October spend $0.02, credit balance $14.98. Note the account limit is $100, not the planned $20 cap (see below).*

## To close the remaining items (needs you)
1. In the OpenAI dashboard (Settings → Limits): lower the monthly spend limit from $100 to $20, confirm auto-recharge is off, and add a new screenshot to `docs/gate1-evidence/`.
2. Run the register → logout → login → add lead → refresh tab → generate draft loop once in the browser and save a recording or screenshots to `docs/gate1-evidence/`.
3. Decide on N3 (accept the cold-start latency, or add a warm-up / lower the timeout).
4. Optionally follow `README.md` on a second machine or clean profile.

After 1–3, tick the remaining §5 boxes (login/session and latency) and Phase 1 meets the remediation exit criteria.
