# Gate 1 Rehearsal Evidence (automated portion)

**Date:** 2026-10-07 · **Branch:** `verify/gate1-gap-reanalysis` · **Method:** API started with `dotnet run` against a brand-new LocalDB database (`ZenLeadFreshCheck`, created by `dotnet ef database update`, dropped afterwards), driven with `curl`. No manual DB seeding. Real OpenAI calls (gpt-4o) were used for the timing runs.

## Fresh-database migration
`InitialCreate` → `HardenSchema` → `AddAiUsageLog` applied cleanly with no manual steps. Foreign keys present on `Leads`, `RefreshTokens`, `AspNetUsers`, `AiUsageLogs`.

## Compose latency: register → add lead → generate draft (5 runs, new workspace each run)

| Run | Compose (ms) | Full loop (ms) | Tokens |
|---|---|---|---|
| 1 (cold start) | 5201 | 6224 | 273 |
| 2 | 2577 | 3586 | 304 |
| 3 | 2235 | 3247 | 297 |
| 4 | 2289 | 3386 | 298 |
| 5 | 2106 | 3057 | 302 |

Median compose ≈ 2.3 s; max 5.2 s (first call after process start). 4 of 5 runs well under the ~5 s criterion; the cold first call slightly exceeded it.

## Spend (this rehearsal)
`AiUsageLogs`: 5 calls, 885 prompt + 589 completion tokens, estimated **$0.0081**. The authoritative figure is the OpenAI dashboard (see below).

## OpenAI dashboard check (screenshot: [openai-dashboard-2026-10-07.png](openai-dashboard-2026-10-07.png))
- Organisation "Zenrax Consulting Services", last 24 h: **11 requests, 1,811 tokens**. October spend **$0.02**. Credit balance **$14.98**.
- The app's own log shows 5 calls / 1,474 tokens for the rehearsal; the other 6 requests and ~337 tokens are earlier manual dev calls made before usage was persisted. Same order of magnitude, so the cost estimate looks sound (not reconciled call-by-call).
- The spend limit visible in this screenshot was $100.00. It has since been lowered to the $20 hard cap the plan requires (PBI 5.1), as confirmed by the owner; this screenshot predates that change.

## Regression probes (live)

| Probe | Result |
|---|---|
| Register with `password1` (previously orphaned a workspace) | 400, 0 workspaces created ✅ |
| Register with an existing email | 409, workspace count unchanged ✅ |
| Cross-tenant `GET /leads/{id}` (workspace B → A's lead) | 404 ✅ |
| Cross-tenant `POST /ai/compose-email` | 404 ✅ |
| `GET /leads` with no token | 401 ✅ |
| Replay of a rotated refresh token | 401 ✅ |
| 4 simultaneous refreshes of one token | exactly one 200, three 401 ✅ |
| `Context` > 1000 chars | 400 ✅ |
| 12 compose requests/minute from one workspace | the 10-per-minute window (1 earlier request + 9 here) returned 400 (validation), then 429 with `Retry-After: 60`; other workspace unaffected ✅ |
| 25 bad logins from one IP | 401 ×5, then 429 ✅ |
| `GET /ai/token-usage` for a workspace with no calls | `{"calls":0,"totalTokens":0,"estimatedCostUsd":0}` (scoped) ✅ |
| Start with `OpenAI:ApiKey` empty and a short signing key | Exits with a message naming both problems ✅ |
| `/openapi/v1.json`, `/scalar` | 200 / 302 ✅ |

## Browser rehearsal (Playwright, real Chrome, real UI)
Script: [tools/gate1-rehearsal](../../tools/gate1-rehearsal/README.md). Fresh database, Angular dev server proxying to the API. Screenshots and `results.json` in [browser/](browser/). Final run: **13/13 steps passed**, repeated twice.

| Step | Result |
|---|---|
| Register in the UI lands on `/leads` already authenticated | ✅ |
| Empty state for a new workspace; toolbar visible | ✅ |
| Duplicate email shows "That email is already registered. Try logging in instead." | ✅ |
| Logout → `/login`, toolbar hidden; login again → `/leads` | ✅ |
| Add lead; it is listed | ✅ |
| Browser refresh keeps the session (one silent `/auth/refresh`, still on `/leads`) | ✅ |
| Generate draft: personalised, editable subject/body, **1.9 s and 3.9 s** click-to-rendered in the two clean runs | ✅ |
| Back link; with no refresh token a protected page redirects to `/login` | ✅ |

**Defect found and fixed by this run:** the first attempt showed `/leads` stuck on "Loading…" although the API returned 200. The app runs zoneless (Angular 21 default, no zone.js), so plain-property updates in HTTP callbacks never re-rendered; manual clicking/typing hid it. Fixed with `ChangeDetectorRef.markForCheck()` in `LeadsList`, `LeadDetail`, `Login` and `Register`, plus a regression spec (`leads-list.spec.ts`) that fails without the fix.

## Items confirmed by the owner (no automated evidence)
- OpenAI monthly limit lowered to the planned $20 hard cap.
- The `README.md` setup was followed successfully on a second machine / clean profile.
- Parallel-401 handling in a real browser remains covered by unit tests only.
