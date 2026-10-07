# ZenLead — MVP Implementation Plan
**Stack: ASP.NET Core (hosting the Angular SPA in a single project), deployed to Azure as one App Service**
**Scope: Proposal Phase 1 (Proof of Concept) + Phase 2 (Minimum Viable Product) only.** Phase 3 onward (LinkedIn, WhatsApp/SMS, CRM pipeline, Stripe billing, white-label) is explicitly out of scope for this plan.

---

## 0. Scope boundary

### In scope (Phase 1 + Phase 2)
- Workspace signup/login, team invites with two roles: Owner, Member
- Lead & company records, CSV import with column mapping and dedup
- AI-personalised email drafting (GPT-4o via a Semantic Kernel abstraction)
- Simple email-only sequences (fixed-delay steps, no branching)
- Sending engine (SendGrid) with scheduled follow-ups
- Unified inbox for email replies, with AI reply classification (interested / not interested / unsubscribe)
- Basic analytics: sent / opened / replied / bounced, per campaign and workspace
- Deployed to Azure, usable end-to-end by real people

### Deferred to Phase 3–4 (do not build yet)
- LinkedIn automation (Unipile), WhatsApp, SMS channels
- Visual CRM pipeline / deal stages
- Stripe billing, plan enforcement, metering
- Agency white-label, custom domains, PDF reports
- Granular RBAC, GDPR/PDPL compliance tooling
- AI lead scoring beyond reply classification
- CRM integrations (HubSpot, Salesforce, Pipedrive), public API

The proposal's own Gate 2 criteria — "MVP deployed and tested by at least 3 internal users" — is the finish line for this plan. Anything not needed to clear that gate is parked, not forgotten.

---

## 1. Architecture overview

**One project, one deployable.** This plan uses the ASP.NET Core + Angular project template: the Angular app lives in a `ClientApp/` folder inside the ASP.NET Core project, and its production build output is served by the same Kestrel process (as static files from `wwwroot`) that serves the API. There is no separate Angular hosting product — one Azure App Service runs both.

```
                 ┌─────────────────────────────────────┐
   Browser  ⇄    │   ASP.NET Core Web App (App Service) │  ⇄  Azure SQL
                 │   ├─ wwwroot/  (built Angular SPA)    │      (EF Core, shared schema,
                 │   ├─ Controllers/  (REST API, JWT)    │       WorkspaceId per row)
                 │   └─ in-process Hangfire server        │
                 └───────────────────┬───────────────────┘
        ┌─────────────────┬──────────┼───────────────────┬─────────────────┐
   SendGrid          OpenAI GPT-4o                   Azure Blob         Key Vault /
(send + inbound      (via Semantic                   (CSV staging)      App Insights
   parse)              Kernel)
```

In development, `ng serve` still runs standalone with a proxy to the API for hot reload (the standard SPA dev-server setup); in production there's a single published artifact and a single App Service to operate.

**Why a monolith, not microservices — and why one deployable, not two:** at 2–3 developers and a $105–175/month infra budget, a single deployable keeps operational overhead and cost near zero (one App Service, one CI/CD pipeline, one set of logs). The Domain / Application / Infrastructure layering below keeps the code separable later without paying for distributed-systems complexity now.

---

## 2. Solution & repo structure

`ZenLead.sln`, generated from the ASP.NET Core + Angular template and then split into layers:

- **ZenLead.Api** — Controllers, JWT auth, Swagger, Hangfire dashboard, **and `ClientApp/` (the Angular project)**. `SpaProxy` forwards to `ng serve` in development; `ng build` output is published into this project's `wwwroot/` for production, so `dotnet publish` produces the one deployable artifact.
  - `ClientApp/src/app/core/` — Auth guard, JWT interceptor, typed API clients
  - `ClientApp/src/app/features/auth` — Register, login, workspace onboarding
  - `ClientApp/src/app/features/leads` — List, detail, CSV import wizard
  - `ClientApp/src/app/features/campaigns` — List, step builder, enrollment view
  - `ClientApp/src/app/features/inbox` — Thread list, conversation view
  - `ClientApp/src/app/features/analytics` — Campaign & workspace dashboards
  - `ClientApp/src/app/shared/` — Angular Material theme, reusable components
- **ZenLead.Application** — Use-case services, DTOs, FluentValidation
- **ZenLead.Domain** — Entities, enums, sequencing/scheduling logic
- **ZenLead.Infrastructure** — EF Core DbContext, SendGrid + Semantic Kernel clients, Hangfire jobs
- **ZenLead.Tests** — xUnit: domain rules, CSV parsing, scheduling

Scaffolded in Phase 1, Week 1 (`dotnet new angular`, then the layers pulled out around it), so Phase 2 sprints add features rather than structure.

---

## 3. Phase 1 — Proof of Concept (Weeks 1–3)

Runs locally, no cloud hosting. Goal: prove the stack end-to-end for ~$7–14 in API fees before committing developer time to Phase 2.

| Week | Focus | Deliverable |
|---|---|---|
| 1 | Scaffolding | .NET solution + Angular workspace created. ASP.NET Core Identity for register/login, JWT issuance. Local SQL Server/SQLite with first EF Core migration (Workspace, User, Lead). Angular login/register screens wired to the API. |
| 2 | AI loop | OpenAI GPT-4o wrapped behind a Semantic Kernel abstraction (so the provider can swap to Azure OpenAI later without touching call sites — the proposal's own mitigation for OpenAI pricing risk). `POST /api/ai/compose-email` built. Angular lead-detail screen with a "Generate draft" button. |
| 3 | Demo & gate | Fix rough edges, confirm the full local loop (register → add lead → generate draft) works reliably. Prepare the Gate 1 demo. |

### Gate 1 — Decision: fund Phase 2?
Exit criteria, verified live rather than assumed:
- A user can register a workspace and log back in with a JWT-protected session
- A lead can be created and persisted via EF Core
- The AI compose endpoint returns a usable, personalised draft in under ~5 seconds
- Total spend to this point stays under ~$20 (OpenAI usage only)

---

## 4. Phase 2 — Minimum Viable Product (Weeks 4–13)

Five two-week sprints, 2–3 developers, deployed to Azure throughout. Each sprint ships something demoable rather than landing all at once in week 13.

### Sprint 1 — Foundation (Weeks 4–5)
Provision Azure: one App Service (hosting the combined ASP.NET Core + Angular build), Azure SQL Basic/S0, Key Vault, Application Insights. Stand up GitHub Actions CI/CD — `ng build` runs as a pre-publish step so `dotnet publish` ships one artifact, deployed to one App Service (build → test → publish → deploy on merge to `main`). Extend the domain model to real multi-tenancy: Workspace, WorkspaceMember with role (Owner/Member), invite-by-email flow.

### Sprint 2 — Leads at volume (Weeks 6–7)
CSV import: upload to Blob Storage, column-mapping UI in Angular, background parse job in Hangfire, email-based dedup against existing leads, per-row error reporting. Lead list with search/filter and a detail view.

### Sprint 3 — Campaigns & sending (Weeks 8–9)
Campaign builder: ordered steps with fixed-day delays, subject/body templates, optional AI personalisation per step. Sender domain verification (SPF/DKIM via SendGrid). Hangfire recurring job walks active enrollments and sends due steps through SendGrid, recording `EmailMessage` rows.

### Sprint 4 — Unified inbox (Weeks 10–11)
SendGrid Inbound Parse webhook (needs a subdomain + MX record — **start DNS work at the top of this sprint, not the end**) lands replies as `InboxMessage` rows threaded by lead. GPT-4o classifies each reply as interested / not interested / unsubscribe; unsubscribes auto-pause the enrollment. Angular inbox UI: thread list + conversation view.

### Sprint 5 — Analytics, UAT, hardening (Weeks 12–13)
Per-campaign and workspace-level dashboard (sent, open rate, reply rate, bounce rate) from SendGrid event webhooks. Serilog → Application Insights end-to-end. Internal UAT with at least 3 users running the full loop. Fix, harden, prepare the Gate 2 demo.

### Gate 2 — Decision: begin customer acquisition?
This is the proposal's actual bar — nothing added:
- MVP deployed to Azure and reachable outside the local network
- At least 3 internal users have completed the full loop: import leads → build a campaign → send it → see a reply in the inbox → read the analytics
- Infrastructure spend tracking to the $105–175/month band

---

## 5. Data model

Shared-schema multi-tenancy: every tenant-owned table carries a `WorkspaceId`, enforced by an EF Core global query filter bound to the caller's JWT claim — not by separate databases per tenant.

| Entity | Key fields | Notes |
|---|---|---|
| `Workspace` | Name, PlanTier, TimeZone, Country | Tenant root |
| `WorkspaceMember` | WorkspaceId, UserId, Role, Status | Role: Owner \| Member |
| `Company` | Name, Domain, Industry, Country | Derived from CSV import |
| `Lead` | CompanyId, Name, Email, Title, Status | Status: New → Contacted → Replied → Unsubscribed |
| `CsvImportBatch` | FileName, ColumnMapping (json), RowCount, ErrorLog | One row per upload |
| `Campaign` | Name, Status, FromSenderId | Status: Draft \| Active \| Paused \| Completed |
| `CampaignStep` | Order, DelayDays, SubjectTemplate, BodyTemplate | Email-only, no branching in MVP |
| `CampaignEnrollment` | CampaignId, LeadId, CurrentStep, NextSendAt | Drives the Hangfire sender job |
| `EmailMessage` | SendGridMessageId, SentAt, OpenedAt, BouncedAt | Source for analytics |
| `InboxThread` / `InboxMessage` | LeadId, Direction, Body, Classification | Classification via GPT-4o |
| `AiGenerationLog` | WorkspaceId, Prompt, TokensUsed, Cost | Per-workspace AI cost guardrail |

---

## 6. API surface

REST over JWT bearer auth, versioned from day one as `/api/v1`.

| Area | Endpoints |
|---|---|
| Auth & workspace | `POST /auth/register` · `POST /auth/login` · `POST /auth/refresh` · `POST /workspaces/{id}/invite` |
| Leads | `GET/POST /leads` · `POST /leads/import` · `GET /leads/import/{batchId}/status` |
| Campaigns | `GET/POST /campaigns` · `POST /campaigns/{id}/steps` · `POST /campaigns/{id}/enroll` · `POST /campaigns/{id}/activate` |
| AI | `POST /ai/compose-email` |
| Inbox | `GET /inbox/threads` · `GET /inbox/threads/{id}` · `POST /inbox/threads/{id}/reply` |
| Analytics | `GET /analytics/campaigns/{id}/summary` · `GET /analytics/workspace/summary` |
| Webhooks (unauthenticated, signature-verified) | `POST /webhooks/sendgrid/inbound` · `POST /webhooks/sendgrid/events` |

---

## 7. Third-party integrations & setup

| Service | Used for | Setup step to start early |
|---|---|---|
| OpenAI GPT-4o | Email drafting, reply classification | API key + $20/month cap, wrapped via Semantic Kernel |
| SendGrid | Outbound send, inbound parse, open/click/bounce events | Verify sending domain (SPF/DKIM); configure Inbound Parse MX in Sprint 1, not Sprint 4 |
| Azure App Service | Hosts the combined ASP.NET Core app (API + built Angular SPA) | B1/S1 Linux plan, deployment slots for staging |
| Azure SQL | Primary datastore | Basic/S0 tier is sufficient at MVP volume |
| Azure Key Vault | Secrets (connection strings, API keys) | Never in `appsettings.json` |
| Application Insights | Logs, traces, exceptions | Serilog sink configured from the first deploy |
| Domain name | Sending domain, app URL | ~$15/year, needed before SendGrid domain verification |

---

## 8. Team & roles

The proposal calls for 1–2 developers in Phase 1 and 2–3 in Phase 2. With only 2 developers, one person covers backend + DevOps.

- **Backend / .NET** — API, EF Core, domain logic; Semantic Kernel + SendGrid clients; Hangfire jobs, webhook handling
- **Frontend / Angular** — SPA structure, routing, auth guard; campaign builder & import wizard UX; inbox & analytics views
- **Full-stack / DevOps** — Azure provisioning, CI/CD; monitoring, Key Vault, backups; cross-cuts: auth, tenant isolation tests

---

## 9. Engineering practices

- **Multi-tenancy:** shared database, shared schema. Every tenant table carries `WorkspaceId`; an EF Core global query filter reads it from the JWT claim so a missing filter can't leak another workspace's leads by accident.
- **Security:** ASP.NET Identity password hashing, short-lived JWT access tokens with refresh-token rotation, all secrets in Key Vault, SendGrid webhook signatures verified before processing.
- **Observability:** Serilog structured logging piped to Application Insights; Hangfire's dashboard (auth-protected) as the operational view into scheduled sends and failures.
- **Testing:** xUnit around the things that are actually risky to get wrong — CSV parsing/dedup, sequence-step scheduling, tenant-filter enforcement. Angular gets targeted unit tests on the auth interceptor and step-scheduling display logic, not full coverage. One manually-verified smoke path per release: register → import → campaign → send → reply → analytics.
- **AI cost guardrails:** every OpenAI call is logged with token count and estimated cost against the workspace. A soft per-workspace monthly cap prevents a single runaway campaign from blowing the $20–60/month AI budget the proposal assumes.

---

## 10. Cost recap — Phase 1–2 only

| Phase | Duration | Monthly cost | Phase cost | Cumulative |
|---|---|---|---|---|
| Phase 1 — POC | 3 weeks | ~$10–20 | ~$7–14 | ~$14 |
| Phase 2 — MVP | 9 weeks | ~$105–175 | ~$220–368 | ~$382 |

Plus a one-time ~$15/year domain purchase, needed before Sprint 1's SendGrid domain verification. Twilio 10DLC (~$19) is a Phase 4 cost — SMS is out of scope here.

---

## 11. Build-specific risks

In addition to the proposal's own risk table, these are specific to building this stack on this timeline:

| Risk | Mitigation |
|---|---|
| SendGrid Inbound Parse needs DNS/MX changes that can take days to propagate | Start domain + DNS work in Sprint 1, not Sprint 4 when the inbox feature is due |
| AI spend creeps past the $20–60/month assumption | Per-workspace token cap and cost logging from the first AI call, not retrofitted later |
| CSV data quality (duplicate leads, malformed emails) pollutes the dataset | Dedup and validation are a dedicated Sprint 2 deliverable, not a side effect |
| Tenant isolation bug leaks one workspace's leads into another's view | Global query filter plus a dedicated integration test suite before the Gate 2 demo |
| Structured AI output silently mis-parses (e.g. model returns camelCase JSON, DTO is PascalCase, and default `System.Text.Json` is case-sensitive → null fields) | Deserialize model output with `JsonSerializerDefaults.Web` (case-insensitive), validate required fields explicitly rather than trusting the deserializer, and use camelCase fixtures in parsing tests. Found in Phase 1 Feature 6; applies to every future structured-output call (e.g. Phase 2 reply classification) |
| .NET / Angular version drift across a 3-person team | Pin .NET 10 LTS and Angular 21 in Phase 1 week 1; upgrade only between phases |

---

*Scoped to Proposal Phases 1–2 only. Stack: ASP.NET Core · Angular · Azure SQL · Hangfire · GPT-4o (via Semantic Kernel) · SendGrid.*
