# ZenLead

AI-powered cold-outreach SaaS. Phase 1 (proof of concept): register a workspace, add leads, generate an AI-personalised email draft. See [plans/](plans/) for the roadmap and [CLAUDE.md](CLAUDE.md) for architecture notes.

## Prerequisites

- .NET 10 SDK
- Node.js + npm (Angular 21)
- SQL Server LocalDB
- An OpenAI API key (set a hard monthly usage cap in the OpenAI dashboard before using it)

## Local setup

All secrets live in `dotnet user-secrets` (never `appsettings.json`). From the repo root:

```
dotnet user-secrets init --project ZenLead.Api
dotnet user-secrets set "ConnectionStrings:Default" "Server=(localdb)\mssqllocaldb;Database=ZenLeadDb;Trusted_Connection=True;" --project ZenLead.Api
dotnet user-secrets set "Jwt:SigningKey" "<random string, at least 32 characters>" --project ZenLead.Api
dotnet user-secrets set "Jwt:Issuer" "zenlead" --project ZenLead.Api
dotnet user-secrets set "Jwt:Audience" "zenlead-client" --project ZenLead.Api
dotnet user-secrets set "OpenAI:ApiKey" "<your OpenAI key>" --project ZenLead.Api
dotnet user-secrets set "SendGrid:ApiKey" "<restricted SendGrid key, Mail Send only>" --project ZenLead.Api
dotnet user-secrets set "Unsubscribe:SigningKey" "<random string, at least 32 characters>" --project ZenLead.Api
dotnet user-secrets set "SendGrid:InboundSecret" "<random string, at least 32 characters; required when Email:Provider=SendGrid>" --project ZenLead.Api
dotnet user-secrets set "SendGrid:EventWebhook:PublicKey" "<Verification Key from SendGrid → Mail Settings → Event Webhook; required when Email:Provider=SendGrid>" --project ZenLead.Api
```

Campaigns (F22): `Unsubscribe:SigningKey` signs the unsubscribe link in every campaign email. The links never expire, so **changing this key breaks every link already sent** — set it once per environment and don't rotate it casually. Campaign send windows use the workspace time zone (`PUT /api/v1/workspaces/current/time-zone`) unless a campaign overrides it; activation is refused until `Email:DomainVerified` is `true`.

Sending (F23): the `campaign-sender` Hangfire job runs every minute and mails due campaign steps. **Before enrolling anything real, set `Sending:RecipientAllowList` to your own address or domain** — `Sending:Mode` is `Restricted` by default everywhere, which sends campaign email only to allow-listed recipients (exact addresses or domains), and an empty list sends to nobody; blocked sends fail the enrollment with a clear reason. Only a deliberate `Sending:Mode=Live` mails real prospects, and the API logs the mode as a warning at startup. Example (user-secrets or `appsettings.Development.json`): `dotnet user-secrets set "Sending:RecipientAllowList:0" "you@example.com" --project ZenLead.Api`. Caps are config too: `Sending:WorkspaceDailyCap` (50, new-domain warm-up), `Sending:MaxPerWorkspacePerRun` (20/minute), plus each campaign's `DailySendCap`. Sending is at-most-once: an interrupted send is marked `Failed("Uncertain…")` and never retried. AI personalisation has a per-workspace monthly soft cap, `AiBudget:MonthlyCostCapUsd` (default 20, warning at 80%); the hard ceiling remains the OpenAI dashboard limit.

Email (F17 onwards): `SendGrid:ApiKey` is a restricted key with only Mail Send; never commit it or paste it into `.http` files. `SendGrid:EventWebhookPublicKey` (F28) will go to user-secrets the same way. Non-secret defaults live in `appsettings.json` under `Email` (`FromAddress`, `FromName`, `DomainVerified`, `InboundDomain`); set `Email:DomainVerified` to `true` by hand once SendGrid shows the domain verified. SendGrid account, DNS and Inbound Parse setup is in the runbook `plans/phase-2-features/17-sendgrid-dns.md`; current status is in `docs/sendgrid-dns-status.md`.

Inbound replies (F25): SendGrid Inbound Parse posts every reply to `POST /api/v1/webhooks/sendgrid/inbound?secret=<SendGrid:InboundSecret>` (set that full URL on the Inbound Parse host for `Email:InboundDomain`; locally it goes through the dev tunnel). A wrong secret gets `401`; everything authentic gets `200` — replies are attached to the lead's inbox thread (lead → Replied, sequence stopped; out-of-office and bounce auto-replies are stored but don't stop it), redeliveries are deduped on Message-ID, and anything unmatched, ambiguous, spoof-suspected, unparseable or oversized lands in the `InboundQuarantine` table (headers only, no body). `Inbound:ParseMode` is `Fields` (SendGrid's default parsed fields); `RawMime` switches to MimeKit parsing and must be set together with "POST the raw, full MIME message" on the Inbound Parse host — switch if the log warns about a non-UTF-8 charset. In Development, `Webhooks:CaptureRaw=true` writes each post's fields to `%TEMP%\zenlead-inbound\` for test fixtures (they contain personal data). The secret rides in the query string, so don't enable request-URL logging on the tunnel until F29's redaction covers `secret`.

Delivery events & analytics (F28): in SendGrid → Settings → Mail Settings → Event Webhook set the URL to `https://<tunnel or app host>/api/v1/webhooks/sendgrid/events`, select Delivered, Opened, Bounced, Dropped, Spam Reports and Unsubscribes, enable **Signed Event Webhook Requests**, and copy the Verification Key into `SendGrid:EventWebhook:PublicKey` (user-secrets; never appsettings). Each post is verified (ECDSA over timestamp + raw body, timestamps older than `SendGrid:EventWebhook:MaxAgeHours` = 26 rejected) before anything is parsed — a bad signature gets `401`; authentic batches get `200` and are idempotent on `sg_event_id`. Hard bounces and spam reports stop the sequence, mark the lead and add the address to the suppression list. `/analytics` shows cohort figures (emails sent in the selected days, in the workspace time zone); opens are approximate.

Optional overrides: `OpenAI:Model` (default `gpt-4o`), `OpenAI:PricePer1KInputUsd` and `OpenAI:PricePer1KOutputUsd` (used for the spend estimate), and `OpenAI:WarmUpOnStartup` (default `false`; when `true`, makes one 1-token completion after startup so the first compose request does not pay connection setup. Measured gain was within noise, so it is off by default).

Lead discovery (F14) settings: `LeadSource:Provider` (`Fake` default in Development; `Pdl` required outside it, with `LeadSource:Pdl:ApiKey`), `LeadSource:MonthlyCreditCap` (default 500, per workspace per UTC month, in the provider's credits), `LeadSource:MaxLeadsPerRun` (100) and `LeadSource:PageSize` (25). Background jobs use Hangfire in the same SQL Server database (`HangFire` schema, created on first start); set `Hangfire:Enabled=false` to boot without it. The job dashboard at `/hangfire` is reachable only through the "Job dashboard" account-menu item for emails listed in `Hangfire:AdminEmails` (`dotnet user-secrets set "Hangfire:AdminEmails:0" "you@example.com" --project ZenLead.Api`).

Super admin (F19): the platform account that creates workspaces (and nothing else; it gets 403 on every tenant endpoint). It is created at startup from two secrets that **you choose and share with no one** — there are no defaults anywhere in the repo:

```
dotnet user-secrets set "SuperAdmin:Email" "<your super admin email>" --project ZenLead.Api
dotnet user-secrets set "SuperAdmin:Password" "<at least 12 characters, upper/lower/digit/symbol, not containing the email name>" --project ZenLead.Api
```

Seeding is idempotent: once the account exists, changing `SuperAdmin:Password` does **not** change its password. Without the two keys the app logs a warning in Development and refuses to start in Production (until the account exists). The super admin signs in on the normal login page and lands on *Workspaces*; creating the first workspace (e.g. "Zenrax" with its admin email) happens there. After 5 wrong passwords any account is locked for 15 minutes. `App:PublicBaseUrl` (the SPA's public origin, used for links in emails) is set to `https://localhost:52618` in `appsettings.Development.json` and is required outside Development.

CSV import (F15) stores uploaded files in blob storage. Locally that is Azurite (`Storage:ConnectionString` is `UseDevelopmentStorage=true` in `appsettings.Development.json`; not a secret). Install and start it before uploading:

```
npm i -g azurite
azurite --silent --location %TEMP%\azurite
```

(or use the Azurite component of Visual Studio). In Azure, set `Storage:AccountUri` instead and the app authenticates with `DefaultAzureCredential`. The Azurite tests live in `ZenLead.IntegrationTests` and skip, with a message, when Azurite isn't running.

Outbound email (F18) goes through `Email:Provider`: `Log` (the Development default, set in `appsettings.Development.json`) writes each message, including approval and reset links, to the API console instead of sending it; `SendGrid` sends for real and needs `SendGrid:ApiKey`. Outside Development the provider must be `SendGrid`. To test real delivery locally, `dotnet user-secrets set "Email:Provider" "SendGrid" --project ZenLead.Api`, then (signed in as an address in `Hangfire:AdminEmails`) call `POST /api/v1/ops/test-email`: it mails the caller's own address.

The API refuses to start, with a message naming each missing setting, if any required value is absent or the signing key is shorter than 32 bytes.

Create the database:

```
dotnet ef database update -p ZenLead.Infrastructure -s ZenLead.Api
```

## Run

```
dotnet run --project ZenLead.Api
```

This also starts the Angular dev server through the SPA proxy. Open https://localhost:7242 (API) or https://localhost:52618 (Angular dev server).

In Development, an interactive API reference is served at `/scalar` (use the Authorize button with an access token from `/api/v1/auth/login`). Ready-made requests are in `ZenLead.Api/ZenLead.Api.http`.

Passwords must be at least 8 characters with a digit, a lowercase and an uppercase letter, and a symbol.

## Test

```
dotnet test ZenLead.Tests
cd ZenLead.Client && npm test
dotnet test ZenLead.IntegrationTests     # real SQL Server (LocalDB), Hangfire, Azurite, stubbed SendGrid/OpenAI/PDL
```

Test categories (`[Trait("Category", …)]`, constants in `ZenLead.Tests/Support/TestCategories.cs`): untagged tests are unit tests and run by default; `Integration`, `Azurite`, `Performance` and `Live` are opt-in. The integration tests (T03) are their own project, `ZenLead.IntegrationTests`: they need LocalDB (or `ZENLEAD_TEST_SQL=container` and Docker) and Azurite for the blob tests, and never call a real provider. `Live` tests call real providers and never run by default. See `docs/testing/README.md`.

### E2E environment

`ASPNETCORE_ENVIRONMENT=E2E` runs the API for automated browser and API tests (T00). It uses only fakes: emails are written as JSON files to `Email:PickupDirectory` (`.e2e/mail` at the repo root), AI and lead discovery are deterministic fakes (`Ai:Provider=Fake`, `LeadSource:Provider=Fake`), Hangfire is off and enqueued jobs wait until a test runs them, and sending is `Restricted` to `*@example.test`. Startup refuses a SendGrid, OpenAI or PDL key in this environment. Non-secret settings are in `ZenLead.Api/appsettings.E2E.json`; user-secrets are not read.

The runner passes the secrets as environment variables, generated per run:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'E2E'
$env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:Unsubscribe__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:SendGrid__InboundSecret = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$env:SuperAdmin__Email = 'platform-admin@example.test'
$env:SuperAdmin__Password = 'Zl!' + [Guid]::NewGuid().ToString('N') + 'A1'
# optional, for Event Webhook tests: the base64 SPKI public key whose private key the test signs with
# $env:SendGrid__EventWebhook__PublicKey = '<base64>'
dotnet run --project ZenLead.Api --no-launch-profile
```

It uses the LocalDB database `ZenLeadE2E` and needs Azurite running for CSV uploads. The test-support endpoints exist only in this environment and only answer loopback callers:

| Endpoint | Does |
|---|---|
| `POST /api/v1/test/reset` | Drop and migrate `ZenLeadE2E`, clear the pickup folder and the job queue, reset the clock |
| `POST /api/v1/test/seed/{empty\|gate2\|inbox\|analytics}` | Seed a scenario with fresh ids; returns the ids, emails and generated passwords |
| `GET /api/v1/test/jobs` · `POST /api/v1/test/jobs/run-pending[?type=csv\|discovery\|classify]` | List / run the jobs the app enqueued |
| `POST /api/v1/test/jobs/campaign-sender/run` | One run of the campaign sender |
| `GET /api/v1/test/clock` · `POST /api/v1/test/clock/advance {"minutes":n}` · `POST /api/v1/test/clock/set {"utc":"…"}` | Read and move the app's clock |

The fake reply classifier reads markers in the reply text: `[[unsubscribe]]`, `[[ooo]]`, `[[not-interested]]`, `[[review]]` (an uncertain unsubscribe that needs review); anything else is Interested.

## Notes

- Rate limits: `POST /api/v1/ai/compose-email` is limited to 10 requests/minute per workspace; auth endpoints to 20/minute per client IP. Exceeding returns `429` with `Retry-After`.
- Account lockout is intentionally not enabled in Phase 1; login throttling is the only brute-force control (revisit in Phase 2 hardening).
- AI spend is recorded per call in the `AiUsageLogs` table; `GET /api/v1/ai/token-usage` returns the caller's workspace totals with an estimated USD cost. The OpenAI dashboard remains the authoritative figure.
