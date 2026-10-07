# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project state

ZenLead is an AI-powered cold-outreach SaaS (leads → AI-personalised email campaigns → unified inbox → analytics). **Phase 1 (proof of concept) is built and being hardened for Gate 1**; everything beyond it (campaigns, inbox, analytics, SendGrid, Azure) is not started:

- `ZenLead.Api` — `AuthController` (register/login/refresh), `LeadsController`, `AiController` (compose-email, workspace-scoped token-usage), all under `/api/v1`. Startup fails fast on missing secrets (`StartupConfiguration`). Rate limits: compose per workspace, auth per IP.
- `ZenLead.Application` / `Domain` / `Infrastructure` — auth use cases, `ComposeEmailUseCase` behind `IEmailComposer`, EF Core + Identity, refresh-token rotation, persisted AI usage log (`AiUsageLog`). Migrations: `InitialCreate`, `HardenSchema`, `AddAiUsageLog`.
- `ZenLead.Client` — register/login, leads list, lead detail with "Generate draft", logout toolbar.

Local setup (user-secrets keys, migrations) is in `README.md`. Gap analysis and remediation history: `docs/phase-1-gap-analysis.md`, `plans/phase-1-gap-remediation-plan.md`.

Before implementing a feature, check `plans/phase-1-poc-implementation-plan.md` for the day-by-day plan and current decisions — it is the source of truth for what should exist next and in what order, and it explicitly tracks "current state" and "explicitly out of scope" so work doesn't drift ahead of or behind the plan.

## Commands

### Backend (.NET, from repo root)
```
dotnet build ZenLead.slnx                    # build everything
dotnet run --project ZenLead.Api             # run the API (also auto-launches the Angular dev server via SpaProxy)
dotnet test ZenLead.Tests                    # run all backend tests
dotnet test ZenLead.Tests --filter FullyQualifiedName~ClassName.MethodName   # run a single test
dotnet ef migrations add <Name> -p ZenLead.Infrastructure -s ZenLead.Api    # add an EF Core migration (once EF Core is wired up)
dotnet ef database update -p ZenLead.Infrastructure -s ZenLead.Api
dotnet user-secrets set <Key> <Value> --project ZenLead.Api   # local secrets (JWT signing key, OpenAI key, connection strings — never appsettings.json)
```
API runs at `https://localhost:7242` / `http://localhost:5114` (see `ZenLead.Api/Properties/launchSettings.json`). Manual endpoint checks go in `ZenLead.Api/ZenLead.Api.http`.

### Frontend (Angular, from `ZenLead.Client/`)
```
npm start          # ng serve with local HTTPS certs (wraps ng serve --ssl via aspnetcore-https.js)
ng build            # production build
ng build --watch --configuration development
ng test             # unit tests (Vitest runner)
```
Dev server runs on port 52618. `src/proxy.conf.js` proxies everything under `/api` to the API, so new `/api/v1/...` controllers need no proxy change; any path outside `/api` must be added to that list or `ng serve` requests to it will 404.

Running `dotnet run --project ZenLead.Api` starts `ng serve` for you automatically via the SPA proxy (`SpaProxyLaunchCommand` in `ZenLead.Api.csproj`); you don't need to run both manually during development.

## Architecture

### Layering (Clean Architecture, one deployable)
Dependencies point one way only:
```
ZenLead.Domain  ←  ZenLead.Application  ←  ZenLead.Infrastructure  ←  ZenLead.Api  →  ZenLead.Client (Angular)
                                                                        ↑
                                                              ZenLead.Tests (references all three backend layers)
```
- **ZenLead.Domain** — entities, enums, scheduling/sequencing logic. No dependencies on other projects.
- **ZenLead.Application** — use-case services, DTOs, FluentValidation validators. References Domain only. **Defines the interfaces (e.g. `IEmailComposer`) that Infrastructure implements** — this is how the AI provider and other external services stay swappable without touching use-case code.
- **ZenLead.Infrastructure** — EF Core `DbContext`, SendGrid client, Semantic Kernel/OpenAI client, Hangfire jobs. Implements Application's interfaces.
- **ZenLead.Api** — controllers, JWT auth wiring, DI composition root, and hosts the built Angular SPA. References Application, Infrastructure, and the Client project (for the SPA build, not the assembly).
- **ZenLead.Client** — Angular 21 app, NgModule-based (not standalone components — see `angular.json` schematics config).

There is **one deployable artifact**: `dotnet publish` on `ZenLead.Api` builds the Angular app and serves it from `wwwroot` via `MapFallbackToFile("/index.html")` in `Program.cs`. This is a deliberate cost/ops tradeoff (one App Service, one CI/CD pipeline) documented in `plans/zen-lead-mvp-implementation-plan.md` — don't split this into separate frontend/backend deployables without checking that plan.

### Angular feature layout (`ZenLead.Client/src/app/`)
- `core/` — auth guard, JWT interceptor, typed API clients
- `features/auth`, `features/leads`, `features/campaigns`, `features/inbox`, `features/analytics` — one folder per product area
- `shared/` — Angular Material theme (Azure/Blue, to be added via `ng add @angular/material`), reusable components

### Key architectural decisions already locked in `plans/` (don't re-litigate without checking there first)
- **Multi-tenancy**: shared database/schema, every tenant table carries `WorkspaceId`. Phase 1 does a manual claim check in controllers; an EF Core global query filter reading the JWT `workspace_id` claim is a named Sprint 1 (Phase 2) hardening task, not built yet.
- **Auth**: ASP.NET Core Identity (`IdentityUser<Guid>` subclassed as `AppUser`), JWT access tokens kept **in-memory only** on the Angular side (never persisted), refresh tokens rotated on use and stored (hashed) server-side, raw refresh token in `localStorage` client-side.
- **AI**: OpenAI GPT-4o accessed only through Semantic Kernel, behind an `IEmailComposer` interface defined in `ZenLead.Application` — call sites must never reference Semantic Kernel/OpenAI types directly, so the provider can swap to Azure OpenAI later.
- **API versioning**: all routes under `/api/v1/...` from the first controller.
- **Local dev DB**: SQL Server LocalDB (not SQLite) — Phase 2 targets Azure SQL, so staying on the SQL Server EF Core provider avoids a provider swap later.
- **Secrets**: `dotnet user-secrets` locally (never `appsettings.json`); Key Vault is a later-phase concern.

### Testing conventions (from `plans/`)
- xUnit tests focus on things that are actually risky to get wrong: CSV parsing/dedup, sequence-step scheduling, tenant-filter enforcement, JWT claim contents, refresh-token rotation.
- AI-dependent code is tested against a fake `IEmailComposer` — no real OpenAI calls in CI/tests.
- Angular gets targeted unit tests (auth interceptor, scheduling display logic), not full coverage.

## Where files go
Plans go in `plans/`; reports and other docs (gap analyses, evidence, notes) go in `docs/`.

## Plans folder

`plans/zen-lead-mvp-implementation-plan.md` is the full Phase 1 (PoC) + Phase 2 (MVP) plan: scope boundaries, architecture, data model, API surface, and engineering practices. `plans/phase-1-poc-implementation-plan.md` is the detailed week-by-week, day-by-day breakdown of Phase 1 only, and includes a "current state" section that should be kept in sync with what's actually built — check it before starting work to know what's expected next and what's explicitly out of scope for the current phase.
