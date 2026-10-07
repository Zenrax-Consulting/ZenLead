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
```

Optional overrides: `OpenAI:Model` (default `gpt-4o`), `OpenAI:PricePer1KInputUsd` and `OpenAI:PricePer1KOutputUsd` (used for the spend estimate).

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
