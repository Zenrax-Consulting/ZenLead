# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project state

ZenLead is an AI-powered cold-outreach SaaS (leads → AI-personalised email campaigns → unified inbox → analytics). **Phase 1 (PoC) is complete. Phase 2 (MVP) Features 11–28 are merged**: tenancy, leads, discovery, CSV import, SendGrid email, accounts, campaigns, the sending engine, the inbox and analytics. **Next: F29 hardening, F30 Azure + CI/CD, F31 UAT / Gate 2** (`plans/phase-2-features/29-…`, `30-…`, `31-…`). Nothing is deployed yet; it runs locally only.

What exists, by area. All routes are under `/api/v1`; controllers live in `ZenLead.Api/Controllers/V1/`.
- **Accounts & tenancy.** `AuthController` covers register (pending until approved), approve/reject via emailed token, login, refresh, forgot/reset/change password. `WorkspacesController` is the public workspace picker plus the current workspace's time zone. `Admin/` holds `/admin/workspaces`, for the super admin only. Users have an `AppUserStatus` (`PendingApproval|Active|Rejected|Disabled`). A platform `SuperAdmin` role exists, seeded from config by `SuperAdminSeeder`, with no workspace.
- **Leads.** `LeadsController` (list/search/filter/paging, soft delete), `CompaniesController`, `TargetProfilesController` + `DiscoveryController` (lead discovery via `ILeadSource`: `FakeLeadSource` in dev, `PdlLeadSource` outside it), `LeadImportController` (CSV import, processed by a Hangfire job, files in Azure Blob storage).
- **Campaigns & sending.** `CampaignsController`: campaigns, steps, enrollments, activate/pause. Scheduling (`Domain/Scheduling`) and templates (`Domain/Templates`) are pure Domain code. The `CampaignSenderJob` Hangfire job runs every minute; `Sending:Mode=Restricted` with an allow-list is the default. `UnsubscribeController` uses signed tokens and feeds a suppression list.
- **Inbox.** `Webhooks/` handles SendGrid Inbound Parse (secret in the URL, idempotent on Message-ID, unmatched mail goes to `InboundQuarantine`). Replies are classified by `ClassifyReplyJob` → `IReplyClassifier` (Semantic Kernel). `InboxController`: threads, read state, manual replies, reclassify.
- **Analytics.** Signed SendGrid Event Webhook (ECDSA, idempotent on `sg_event_id`) + `AnalyticsController` (workspace and per-campaign summaries).
- **AI.** `AiController` compose-email + usage. Every OpenAI call is logged in `AiUsageLog` (with `Purpose`) and checked against a per-workspace monthly budget (`IAiBudget`).
- **Ops.** `OpsController` (test email); the Hangfire dashboard at `/hangfire` is limited to `Hangfire:AdminEmails`.
- **Client.** Public pages: login, register, approve, forgot/reset password, unsubscribe. Lazy `leads`, `campaigns`, `inbox`, `analytics` modules inside the shell; `admin/workspaces` (super admin); `account/password`.

Migrations run from `InitialCreate` through `AddWebhookEventsAndAnalyticsIndexes` (see `ZenLead.Infrastructure/Migrations/`). **Local setup lives in `README.md`**: all user-secrets keys, plus the sending, inbound, Hangfire and super-admin settings. `StartupConfiguration` fails fast when required secrets or config are missing. SendGrid DNS status: `docs/sendgrid-dns-status.md`. Open follow-ups: `docs/parking-lot.md`.

Before implementing a feature, read its file in `plans/phase-2-features/NN-*.md`. Each lists the files to add or modify, its tests, its verification steps and what is "Not in this feature". Also read the parent `plans/phase-2-mvp-implementation-plan.md` for sprint order and dependencies. These are the source of truth for what comes next. Phase 1 history is in `plans/phase-1-poc-implementation-plan.md` and `plans/phase-1-features/`.

## Commands

### Backend (.NET, from repo root)
```
dotnet build ZenLead.slnx                    # build everything
dotnet run --project ZenLead.Api             # run the API (also auto-launches the Angular dev server via SpaProxy)
dotnet test ZenLead.Tests                    # run all backend tests
dotnet test ZenLead.Tests --filter FullyQualifiedName~ClassName.MethodName   # run a single test
dotnet test ZenLead.IntegrationTests         # integration tests (T03): LocalDB, Hangfire, Azurite, WireMock providers
dotnet ef migrations add <Name> -p ZenLead.Infrastructure -s ZenLead.Api    # add an EF Core migration
dotnet ef database update -p ZenLead.Infrastructure -s ZenLead.Api
dotnet user-secrets set <Key> <Value> --project ZenLead.Api   # local secrets (never appsettings.json)
```
API runs at `https://localhost:7242` / `http://localhost:5114` (see `ZenLead.Api/Properties/launchSettings.json`). Manual endpoint checks go in `ZenLead.Api/ZenLead.Api.http`.

### Frontend (Angular, from `ZenLead.Client/`)
```
npm start          # ng serve with local HTTPS certs (wraps ng serve --ssl via aspnetcore-https.js)
ng build            # production build
ng build --watch --configuration development
ng test             # unit tests (Vitest runner)
```
Dev server runs on port 52618. `src/proxy.conf.js` proxies `/api` and `/hangfire` to the API, so new `/api/v1/...` controllers (webhooks included) need no proxy change. Any other server path must be added to that list or `ng serve` requests to it will 404. SPA routes like `/unsubscribe/:token` are Angular routes and don't need a proxy entry.

Running `dotnet run --project ZenLead.Api` starts `ng serve` for you automatically via the SPA proxy (`SpaProxyLaunchCommand` in `ZenLead.Api.csproj`); you don't need to run both manually during development.

Webhooks (SendGrid Inbound Parse and Event Webhook) reach a local API through the persistent dev tunnel (see `README.md`). `App:PublicBaseUrl` builds the links in emails (approval, reset, unsubscribe).

## Architecture

### Layering (Clean Architecture, one deployable)
Dependencies point one way only:
```
ZenLead.Domain  ←  ZenLead.Application  ←  ZenLead.Infrastructure  ←  ZenLead.Api  →  ZenLead.Client (Angular)
                                                                        ↑
                                                              ZenLead.Tests (references all backend projects)
```
- **ZenLead.Domain** — entities, enums, and pure logic with no I/O: `Scheduling` (send times, send windows, time zones), `Templates` (`{{token}}` rendering), `Sending`, `Inbound` (threading, auto-reply detection), `Leads`, `Discovery`. No dependencies on other projects. Put rules that are risky to get wrong here and unit-test them heavily.
- **ZenLead.Application** — use cases, DTOs, FluentValidation validators, options. References Domain only. **Defines the interfaces Infrastructure implements**: `IEmailComposer`, `IReplyClassifier`, `IEmailSender`, `ILeadSource`, `IAiBudget`, `ISuppressionList`, `IJobScheduler`, `ICurrentWorkspace` and others. This is how external services stay swappable without touching use-case code.
- **ZenLead.Infrastructure** — `ZenLeadDbContext` (EF Core + Identity) in `Persistence/`. Implementations in `Ai/` (Semantic Kernel), `Email/` (SendGrid sender, `LogEmailSender` dev fake, event-signature verifier), `Jobs/` (Hangfire jobs), `Identity/` (JWT, refresh tokens, super-admin seeder), `LeadSources/`, `Csv/`, `Storage/` (Azure Blob).
- **ZenLead.Api** — controllers, JWT auth and `Policies`, rate limiting, Hangfire configuration, the DI composition root (`Program.cs`); also hosts the built Angular SPA. References Application, Infrastructure, and the Client project (for the SPA build, not the assembly).
- **ZenLead.Client** — Angular 21 app, NgModule-based (not standalone components — see `angular.json` schematics config).

There is **one deployable artifact**: `dotnet publish` on `ZenLead.Api` builds the Angular app and serves it from `wwwroot` via `MapFallbackToFile("/index.html")` in `Program.cs`. This is a deliberate cost/ops tradeoff (one App Service, one CI/CD pipeline) documented in `plans/zen-lead-mvp-implementation-plan.md` — don't split this into separate frontend/backend deployables without checking that plan.

### Angular feature layout (`ZenLead.Client/src/app/`)
- `core/` — `auth/` (guards, JWT interceptor, auth/approvals services, JWT-claim helpers), `layout/` (shell, AI-budget banner, workspace/ops services), typed API clients
- `features/` — one folder per area: `auth`, `public` (unsubscribe), `leads`, `campaigns`, `inbox`, `analytics`, `admin` (super admin), `account`. Product areas are lazy NgModules registered in `app-routing-module.ts`.
- `shared/` — `SharedModule` (common Material imports). The Angular Material theme is in `src/material-theme.scss`.

### Key architectural decisions already locked in `plans/` (don't re-litigate without checking there first)
- **Multi-tenancy**: one shared database and schema. Tenant entities implement `ITenantEntity` and carry `WorkspaceId`. `ZenLeadDbContext` applies a global query filter from the current workspace (the JWT `workspace_id` claim via `ICurrentWorkspace`) and stamps `WorkspaceId` on insert. **Code with no HTTP context** (Hangfire jobs, webhooks) takes `workspaceId` as an explicit argument and scopes with `.ForWorkspace(workspaceId)` (`TenantQueryExtensions`). Never read the workspace from ambient state, and never take it from request input. Isolation tests guard this.
- **Accounts**: users join an existing workspace by registering, and that workspace's admin (`Workspace.AdminEmail`) approves them through an emailed single-use link. Only the super admin creates workspaces; no seeder or migration creates one. The super admin has no `workspace_id` claim. The default `[Authorize]` policy requires that claim, so the super admin gets 403 on every tenant endpoint. Use `Policies.AnyUser` for endpoints without a workspace and `Policies.SuperAdminOnly` for `/admin/**`. No default credentials anywhere in the repo.
- **Auth**: ASP.NET Core Identity (`IdentityUser<Guid>` subclassed as `AppUser`). JWT access tokens are kept **in memory only** on the Angular side (never persisted). Refresh tokens are rotated on use and stored hashed server-side; the raw refresh token lives in `localStorage` client-side. Approval, password-reset and unsubscribe tokens are random and stored hashed in their own tables, not ASP.NET data-protection tokens. Revoke all refresh tokens when a user's status leaves `Active` or their password is reset.
- **AI**: OpenAI GPT-4o, accessed only through Semantic Kernel behind Application interfaces (`IEmailComposer`, `IReplyClassifier`). Call sites must never reference Semantic Kernel/OpenAI types directly, so the provider can swap to Azure OpenAI later. Every call goes through `IAiBudget` and writes an `AiUsageLog` row. Untrusted text (replies) goes to the model inside a delimited block, and output is validated against an enum: deserialize with `JsonSerializerDefaults.Web` and never throw into the webhook path.
- **Email**: everything outbound goes through `IEmailSender`. `Email:Provider=Log` (the Development default) prints messages, including approval and reset links, to the console. `SendGrid` is required outside Development. Campaign sending is at-most-once (an uncertain send is marked failed, never retried) and checks the suppression list first. `Sending:Mode=Restricted` only mails allow-listed recipients until someone deliberately sets `Live`.
- **Background work & webhooks**: Hangfire, stored in the same SQL Server database (`HangFire` schema). Webhooks return 200 fast and hand slow work (like classification) to a job. Every webhook is authenticated (inbound: secret in the URL; events: ECDSA signature + timestamp) and idempotent.
- **API versioning**: all routes under `/api/v1/...`.
- **Local dev DB**: SQL Server LocalDB (not SQLite) — Phase 2 targets Azure SQL, so staying on the SQL Server EF Core provider avoids a provider swap later.
- **Secrets**: `dotnet user-secrets` locally (never `appsettings.json`); Key Vault arrives with F30.

### Testing conventions (from `plans/`)
- xUnit tests focus on things that are actually risky to get wrong: tenant isolation, scheduling across time zones/DST and send windows, send idempotency and caps, suppression, token flows (approval, reset, refresh rotation), webhook auth and idempotency, CSV parsing/dedup, JWT claims, the authorization endpoint matrix.
- No real external calls in tests: fake `IEmailSender`, `IEmailComposer`, `IReplyClassifier` and `ILeadSource`; captured payload fixtures for webhooks (`ZenLead.Tests/Fixtures/`); test keys for signatures. Live-AI tests are skipped by default.
- Endpoint tests go through `ApiFactory` (`Microsoft.AspNetCore.Mvc.Testing`, SQLite). Tests that need real SQL Server, Hangfire, Azurite or provider HTTP stubs go in `ZenLead.IntegrationTests` (`SqlServerFixture`, `IntegrationApiFactory`, `HangfireTestServer`, `ProviderStubs`; see `docs/testing/README.md`).
- Angular gets targeted unit tests (auth interceptor/guards, pure helpers like schedule display and wizard mapping), not full coverage.

### Working in parallel
Every feature that touches the data model adds an EF migration, and every migration rewrites `ZenLeadDbContextModelSnapshot.cs`. When two branches each add one, merge them in feature-number order. The later branch rebases, deletes its migration and re-runs `dotnet ef migrations add` instead of hand-merging the snapshot. Worktrees go under `D:\Development\Worktrees\ZenLead\<name>`, one branch and one PR per feature.

## Where files go
Plans go in `plans/`; reports and other docs (gap analyses, evidence, notes) go in `docs/`.

## Plans folder

- `plans/zen-lead-mvp-implementation-plan.md` — the full Phase 1 + Phase 2 plan: scope, architecture, data model, API surface, engineering practices.
- `plans/phase-2-mvp-implementation-plan.md` — Phase 2 sprints, features 11–31 with their PBIs, risks and exit criteria.
- `plans/phase-2-features/NN-*.md` — one detailed spec per Phase 2 feature (files, tests, verification, out of scope). Check the relevant one before starting work.
- `plans/phase-1-poc-implementation-plan.md`, `plans/phase-1-features/`, `plans/phase-1-gap-remediation-plan.md` — Phase 1 history.
