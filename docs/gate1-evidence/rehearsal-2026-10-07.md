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
`AiUsageLogs`: 5 calls, 885 prompt + 589 completion tokens, estimated **$0.0081**. Authoritative spend must still be read from the OpenAI dashboard (not done here).

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

## Not covered here (needs a person)
- OpenAI dashboard: hard $20 cap screenshot and actual spend.
- Browser walkthrough with screenshots or a recording: register in the UI, refresh the tab (silent re-auth), empty state, logout toolbar, parallel-401 behaviour.
- A run on a second machine / clean profile following only the README.
