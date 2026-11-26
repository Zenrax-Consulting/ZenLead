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
```

Campaigns (F22): `Unsubscribe:SigningKey` signs the unsubscribe link in every campaign email. The links never expire, so **changing this key breaks every link already sent** — set it once per environment and don't rotate it casually. Campaign send windows use the workspace time zone (`PUT /api/v1/workspaces/current/time-zone`) unless a campaign overrides it; activation is refused until `Email:DomainVerified` is `true`.

Email (F17 onwards): `SendGrid:ApiKey` is a restricted key with only Mail Send; never commit it or paste it into `.http` files. Later features add `SendGrid:InboundSecret` (F25) and `SendGrid:EventWebhookPublicKey` (F28) to user-secrets the same way. Non-secret defaults live in `appsettings.json` under `Email` (`FromAddress`, `FromName`, `DomainVerified`, `InboundDomain`); set `Email:DomainVerified` to `true` by hand once SendGrid shows the domain verified. SendGrid account, DNS and Inbound Parse setup is in the runbook `plans/phase-2-features/17-sendgrid-dns.md`; current status is in `docs/sendgrid-dns-status.md`.

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

(or use the Azurite component of Visual Studio). In Azure, set `Storage:AccountUri` instead and the app authenticates with `DefaultAzureCredential`. The Azurite round-trip tests are skipped unless `AZURITE=1` is set.

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
```

## Notes

- Rate limits: `POST /api/v1/ai/compose-email` is limited to 10 requests/minute per workspace; auth endpoints to 20/minute per client IP. Exceeding returns `429` with `Retry-After`.
- Account lockout is intentionally not enabled in Phase 1; login throttling is the only brute-force control (revisit in Phase 2 hardening).
- AI spend is recorded per call in the `AiUsageLogs` table; `GET /api/v1/ai/token-usage` returns the caller's workspace totals with an estimated USD cost. The OpenAI dashboard remains the authoritative figure.
