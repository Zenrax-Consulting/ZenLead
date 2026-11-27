# T01 — Local Testing

**Branch:** `test/local-env` · **Size:** S · **Depends on:** nothing (starts day 1)

## Goal
Any developer can bring up a clean, known local environment with one command, check that it works, and run the manual smoke path for Features 1–28 in about 20 minutes. Today this knowledge is spread across README, the feature "Verification" sections and the parking lot.

## Files to add

### `tools/local-env/` (PowerShell, Windows-first; each script is idempotent and prints what it did)
| Script | Does |
|---|---|
| `doctor.ps1` | Checks prerequisites and prints ✅/❌ with a fix for each: .NET SDK version from `global.json` (or the csproj target), Node/npm version, LocalDB instance `mssqllocaldb` running, Azurite reachable on 10000 (or `npx azurite` available), dev HTTPS cert trusted (`dotnet dev-certs https --check --trust`), required user-secrets present by **name only** (`Jwt:SigningKey`, `OpenAI:ApiKey`, `Unsubscribe:SigningKey`, `SuperAdmin:Email`/`Password`; SendGrid keys only when `Email:Provider=SendGrid`). It never prints values. Also warns if `Sending:Mode` is `Live` in user-secrets. |
| `reset-db.ps1 [-Name ZenLead]` | Drops and recreates the named LocalDB database, then runs `dotnet ef database update`. Refuses a database name that isn't LocalDB, so it can't drop anything remote. |
| `start-deps.ps1` | Starts Azurite in the background (`npx azurite --silent --location .azurite`) and creates the `imports` container. |
| `start-app.ps1 [-Published]` | Default: `dotnet run --project ZenLead.Api` (SPA proxy starts `ng serve`). `-Published`: `ng build` → `dotnet publish -c Release -o .publish` → run `.publish/ZenLead.Api.dll` with `ASPNETCORE_ENVIRONMENT=Production`-like settings from env vars. That's the build T06 and T10 point at. |
| `seed.ps1 -Scenario gate2` | Starts the API in `E2E` against a separate `ZenLeadLocalSeed` DB and calls the T00 seed endpoint. Prints the logins it created to the console only. Gives manual testers realistic data without CSV gymnastics. |
| `stop-all.ps1` | Stops Azurite, the API and `ng serve` started by these scripts (tracked PIDs in `.local-env/pids.json`). |

### Docs
| File | Content |
|---|---|
| `docs/testing/local-smoke-checklist.md` | The manual smoke path, one line per check with the expected result, grouped by feature. Built from each feature doc's "Verification" section and the parking-lot "Unconfirmed manual checks" (F11 two-workspace check, F12 shell checks, F13 URL-shareable view and cross-page selection, F14 run twice/cap/kill-mid-run, F15/F16 messy CSV end to end, F18 test email, F19 super-admin landing, F20 register→approve, F21 forgot→reset, F22–F24 campaign build/activate, F25 reply via tunnel, F26 classification, F27 inbox reply, F28 events/dashboard). Each line says whether it is **automated elsewhere** (and which test) or **manual only**, so the checklist shrinks as T07 grows. |
| `docs/testing/local-env.md` | Ports (API 7242/5114, SPA 52618, Azurite 10000, E2E 5199/4300), DB names (`ZenLead`, `ZenLeadE2E`, `ZenLeadLocalSeed`, `ZenLeadPerf`), which config each mode uses, the dev tunnel setup for webhooks (F25/F28) with the reminder that the secret travels in the query string so URL logging stays off, and how to switch `Email:Provider` between Log, Pickup and SendGrid safely. |

## Local test layers (the order a developer runs them)
1. **Pre-commit (seconds):** `dotnet build` + `dotnet test ZenLead.Tests --filter "Category!=Integration&Category!=Azurite&Category!=Performance&Category!=Live"` + `ng test --watch=false` on touched specs.
2. **Pre-PR (minutes):** `tools/test-runner/run.ps1 -Pack pr` (T09). Unit, integration on LocalDB, Vitest, Playwright `@smoke` on Chromium.
3. **Before merge of a cross-feature change:** run the earlier feature's suites listed in the phase-2 README "cross-feature touchpoints" table. T09 turns that table into `-Pack touchpoint -Feature F23`.
4. **Manual smoke** (`local-smoke-checklist.md`) before demos and release candidates.

## Verification
- On a fresh clone: `doctor.ps1` names every missing piece. After fixing them, `reset-db.ps1; start-deps.ps1; start-app.ps1` gives a working login page.
- `start-app.ps1 -Published` serves the SPA and `/api/v1` from one port, and a deep link to `/leads/<id>` returns `index.html`.
- `seed.ps1 -Scenario gate2` prints usable logins, and the leads list shows 25 leads.
