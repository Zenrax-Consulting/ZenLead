# ZenLead — Phase 2 (Minimum Viable Product) Implementation Plan
**Scope: Weeks 4–13 of [zen-lead-mvp-implementation-plan.md](zen-lead-mvp-implementation-plan.md) (§4 Phase 2) only.** Five two-week sprints, deployed to Azure throughout. Goal: clear **Gate 2** — MVP live on Azure, ≥3 internal users complete import → campaign → send → reply → analytics, infra spend tracking to $105–175/month.

Format follows [phase-1-poc-implementation-plan.md](phase-1-poc-implementation-plan.md): numbered features continue from Phase 1 (which ended at Feature 10), each is one feature branch + PR, with PBIs underneath. Per-feature file-by-file docs (like `phase-1-features/`) go in `plans/phase-2-features/` and are written at the start of each sprint, not now.

---

## 0. Starting state (Phase 1 outcome — verify at Sprint 1 kickoff)

Built and merged in Phase 1 (Features 1–10):
- Identity (`AppUser`), JWT (claims `sub`, `workspace_id`, `email`) + rotating hashed refresh tokens; `/api/v1/auth/{register,login,refresh}`.
- EF Core on LocalDB: `Workspace`, `Lead` (full `LeadStatus` enum), `RefreshToken`. One workspace per user, no members/roles.
- `LeadsController` (list/create/get) with **manual** `workspace_id` claim check + cross-tenant regression test.
- `IEmailComposer` (Application) → Semantic Kernel/GPT-4o (Infrastructure), `ComposeEmailUseCase`, `POST /api/v1/ai/compose-email`, retry-once, token usage logged to `ILogger` only.
- Angular: Material (Azure/Blue), auth service/interceptor/guard, register/login, leads list (empty state), lead detail with "Generate draft".

**Carry-in checklist (resolve before/at Sprint 1):**
- [ ] Gate 1 outcome recorded and its follow-up fixes triaged into Sprint 1 backlog.
- [ ] `phase-1` "current state" and `CLAUDE.md` "Project state" updated to reflect reality.
- [ ] Domain name purchased (~$15/yr) — blocks SendGrid domain verification and Inbound Parse DNS.
- [ ] Confirm team size (2 vs 3 devs) — drives the parallelism in §6.

---

## 1. Decisions to lock before coding

| Decision | Choice | Why |
|---|---|---|
| Background jobs | **Hangfire, in-process, SQL Server storage** (same Azure SQL DB) | Parent plan §1; no extra infra. Dashboard behind auth (Owner only). |
| Tenant isolation | **EF Core global query filter** on every tenant entity, reading `workspace_id` from an `ICurrentWorkspace` service; Hangfire jobs set workspace explicitly (`IgnoreQueryFilters` + explicit `WorkspaceId`, or a scoped workspace override) | Named Sprint 1 hardening item. Remove manual claim checks only once the filter + test suite is in place. |
| Roles | `WorkspaceMember` with `Owner` \| `Member`; `workspace_id` + `role` claims in JWT | Parent plan §0/§5; no granular RBAC. |
| Invites | Single-use hashed invite token, 7-day expiry, emailed via SendGrid; accepted at `/accept-invite?token=` | Reuses the refresh-token hashing pattern. |
| Email provider | **SendGrid** (v3 send API, Inbound Parse, Event Webhook) behind `IEmailSender` in Application | Same swappable-interface rule as `IEmailComposer`. |
| Webhook auth | SendGrid **signed event webhook** (ECDSA public key verify) for events; shared secret in the Inbound Parse URL for inbound (Parse has no signing) | Webhooks are unauthenticated by JWT; must be verified before any processing. |
| Secrets | Azure Key Vault via managed identity; `user-secrets` stays for local only | Parent plan §7/§9. |
| Logging | Serilog → Application Insights sink from the first deploy | Parent plan §9. |
| Blob staging | CSV uploaded to Azure Blob (`imports/{workspaceId}/{batchId}.csv`), parsed by a Hangfire job; Azurite locally | Keeps large uploads off request threads. |
| Template syntax | Simple `{{firstName}}`, `{{company}}`, `{{title}}` token replacement (no Liquid/Razor) | Enough for MVP; avoids template-injection surface. |
| Email send idempotency | `EmailMessage` row inserted (status `Queued`) **before** calling SendGrid; enrollment advanced in the same transaction as the sent-marking | Prevents double-sends on Hangfire retry. |
| Open tracking | SendGrid open pixel via Event Webhook; treat opens as approximate (Apple MPP) | Report honestly in analytics UI. |
| API versioning/proxy | All new routes under `/api/v1/...`; **add each new path prefix to `ZenLead.Client/src/proxy.conf.js`** | Otherwise `ng serve` 404s. |
| Migrations in prod | Applied by a CI/CD step (`dotnet ef migrations bundle`), never on app start in prod | Avoid slot-swap races and surprise schema changes. |

Note: parent plan §6 lists `POST /ai/compose-email` as `/api/v1`; Phase 1 already followed that.

---

## 2. Sprint 1 — Foundation (Weeks 4–5)

Goal: app running on Azure via CI/CD; real multi-tenancy (members, roles, invites); DNS started for Sprint 4.

### Feature 11 — Azure Provisioning & CI/CD (branch: `feature/azure-cicd`)
- **PBI 11.1 — Infra provisioning** (Bicep or `az` scripts committed under `infra/` so it's reproducible): resource group, App Service plan (B1/S1 Linux) + app + `staging` slot, Azure SQL (Basic/S0), Storage account (Blob), Key Vault, Application Insights/Log Analytics. Managed identity on the App Service with Key Vault `get/list` and Blob data access.
- **PBI 11.2 — Config & secrets**: move `Jwt:*`, `ConnectionStrings:Default`, `OpenAI:ApiKey` into Key Vault (`AddAzureKeyVault` with `DefaultAzureCredential`); startup fails fast if a required secret is missing.
- **PBI 11.3 — GitHub Actions pipeline**: on PR → build + `dotnet test` + `ng test`; on merge to `main` → `dotnet publish` (triggers `ng build` into `wwwroot`) → run EF migrations bundle against Azure SQL → deploy to `staging` slot → smoke check (`/health`) → swap to production. Branch protection requiring green CI.
- **PBI 11.4 — Health & observability baseline**: `/health` endpoint (DB reachable), Serilog + Application Insights sink, request correlation id, CORS/HTTPS/HSTS settings reviewed.
- **PBI 11.5 — Cost tracking**: Azure Cost Management budget alert at ~$175/month (Gate 2 criterion) and OpenAI $20→ per-env cap documented.

### Feature 12 — Tenant Isolation Hardening (branch: `feature/tenant-query-filter`)
Must land **before** any new tenant-owned tables (Features 13+), so each new entity is born filtered.
- **PBI 12.1 — `ICurrentWorkspace`** (Application interface; Api implementation reading the JWT claim; null for unauthenticated/background contexts).
- **PBI 12.2 — Global query filter** on `Lead` (and a convention/base `ITenantEntity { WorkspaceId }` so new entities opt in with one line); auto-stamp `WorkspaceId` on insert in `SaveChanges`.
- **PBI 12.3 — Replace manual checks in `LeadsController`/`ComposeEmailUseCase`** with the filter; keep the Phase 1 cross-tenant regression test and make it run against the filter.
- **PBI 12.4 — Tenant isolation test suite** (xUnit + SQL Server LocalDB/Testcontainers or EF in-memory is **not** acceptable for filters on relational behavior — use real SQL Server): every tenant entity, read/update/delete by foreign id returns not-found; `IgnoreQueryFilters` usages are grep-guarded by a test/analyzer allow-list.

### Feature 13 — Workspace Members, Roles & Invites — backend (branch: `feature/workspace-members-invites`)
- **PBI 13.1 — Domain**: `WorkspaceMember` (WorkspaceId, UserId, Role, Status), `WorkspaceInvite` (WorkspaceId, Email, Role, TokenHash, ExpiresAt, AcceptedAt, InvitedBy). Extend `Workspace` with `PlanTier`, `TimeZone`, `Country` (parent plan §5). Migration: back-fill a `WorkspaceMember(Owner)` row for every existing `AppUser`.
- **PBI 13.2 — Decouple user from a single workspace**: `AppUser.WorkspaceId` becomes "default/last-used workspace"; membership table is the source of truth. JWT carries `workspace_id` + `role`. (Multi-workspace-per-user *switching UI* is out of scope; model must not preclude it.)
- **PBI 13.3 — Invite use cases**: `InviteMemberUseCase` (Owner only, rejects existing member/duplicate pending invite), `AcceptInviteUseCase` (new user sets password or existing user joins), `RevokeInviteUseCase`, `ListMembersUseCase`. `POST /api/v1/workspaces/{id}/invite`, `GET /api/v1/workspaces/{id}/members`, `POST /api/v1/auth/accept-invite`.
- **PBI 13.4 — Authorization policy**: `OwnerOnly` policy on role-claim; apply to invite/members management.
- **PBI 13.5 — `IEmailSender` (transactional) + SendGrid client** with a dev "log to console" fake; invite email template. (Campaign sending in Feature 18 reuses this interface.)
- **PBI 13.6 — Tests**: invite expiry/reuse/wrong-email, Owner-only enforcement, back-fill migration, role claim contents.

### Feature 14 — Members UI & Onboarding (branch: `feature/angular-members`)
- **PBI 14.1** `features/auth/accept-invite` screen; `features/workspace/members` (list, invite form, revoke, role badge — Owner only controls hidden for Members **and** enforced server-side).
- **PBI 14.2** App shell: Material sidenav + toolbar with nav to Leads/Campaigns/Inbox/Analytics (stubs for later sprints), workspace name, logout; role-aware nav.
- **PBI 14.3** Add `/api/v1/workspaces` etc. to `proxy.conf.js`.

### Feature 15 — SendGrid Domain & Inbound DNS Kickoff (branch: `chore/sendgrid-dns` — mostly non-code)
- **PBI 15.1** SendGrid account, API key (Key Vault), domain authentication (SPF/DKIM CNAMEs) for the sending domain.
- **PBI 15.2** Create inbound subdomain (e.g. `reply.<domain>`), **MX record → `mx.sendgrid.net`**, configure Inbound Parse destination URL placeholder. Record DNS TTL/propagation status in `docs/`. *Do this in week 4; Sprint 4 depends on it.*
- **PBI 15.3** Decide and document sender identity model (one verified sender per workspace vs. shared domain with per-workspace from-name) — needed for `Campaign.FromSenderId`.

**Sprint 1 demo / exit:** merge to `main` deploys to Azure automatically; Owner invites a teammate by email, teammate accepts and sees only that workspace's leads; cross-tenant test suite green in CI; DNS records created and propagating.

---

## 3. Sprint 2 — Leads at volume (Weeks 6–7)

### Feature 16 — Company Entity & Lead Model Extension (branch: `feature/company-lead-extension`)
- **PBI 16.1** `Company` (WorkspaceId, Name, Domain, Industry, Country); `Lead.CompanyId` nullable FK; unique index `(WorkspaceId, Email)` on `Lead` (normalised lowercase) — **check for existing duplicates in the migration** and handle them (merge or fail loudly).
- **PBI 16.2** Lead CRUD completion: `PUT /leads/{id}`, `DELETE /leads/{id}` (soft delete preferred so campaign history survives), `Status` transitions rules in Domain.
- **PBI 16.3** `ComposeEmailUseCase` now pulls `Company` data instead of free-text-only context (keep `context?` as an override).

### Feature 17 — CSV Import Backend (branch: `feature/csv-import-backend`)
Highest-risk data-quality feature; test-first.
- **PBI 17.1 — Domain/persistence**: `CsvImportBatch` (FileName, BlobPath, ColumnMapping json, RowCount, ImportedCount, SkippedDuplicateCount, ErrorCount, ErrorLog, Status `Uploaded|Parsing|Completed|Failed`, CreatedBy).
- **PBI 17.2 — Upload & preview**: `POST /api/v1/leads/import` (multipart; size limit e.g. 10 MB / 50k rows, `.csv` only) → store to Blob (`IBlobStorage` in Application, Azure impl + Azurite locally) → return `batchId` + header row + first N sample rows for the mapping UI.
- **PBI 17.3 — Mapping & start**: `POST /api/v1/leads/import/{batchId}/start` with column mapping (name, email, title, company name/domain/industry/country); validate mapping covers required `email`.
- **PBI 17.4 — Parser** (`ZenLead.Domain`/`Application`, pure & unit-tested): streaming CSV read (CsvHelper), BOM/encoding/delimiter handling, trim, email syntax validation + lowercase, in-file duplicate detection, DB duplicate detection (batched `WHERE Email IN (...)`), company upsert by domain/name. Per-row result: `Imported | Duplicate | Invalid(reason)`.
- **PBI 17.5 — Hangfire job** `ProcessCsvImportJob(batchId, workspaceId)`: idempotent (resumable/rerunnable without duplicating leads), batches of ~500 rows per transaction, updates batch counters, writes error log (capped, with row numbers), sets final status. Sets tenant context explicitly.
- **PBI 17.6 — Status & errors**: `GET /api/v1/leads/import/{batchId}/status` (progress, counts), `GET .../errors` (downloadable CSV of rejected rows).
- **PBI 17.7 — Hangfire setup**: SQL storage, auth-protected dashboard (Owner only) at `/hangfire`, retry policy (3 attempts, exponential), dead-letter visibility.
- **PBI 17.8 — Tests (priority)**: malformed quotes, empty file, header-only, missing email column, unicode names, 10k-row perf sanity, in-file dupes, existing-DB dupes, case-differing emails, job re-run idempotence, cross-tenant dedup (same email in two workspaces is **not** a duplicate).

### Feature 18 — Leads UI: Import Wizard & List (branch: `feature/angular-leads-import`)
- **PBI 18.1** Import wizard (Material stepper): upload → map columns (auto-guess by header name) → review → progress (poll status every 2s) → result summary with error-CSV download.
- **PBI 18.2** Leads list upgrade: server-side paging, sort, search (name/email/company), filter by status/company; `GET /leads` takes `page,pageSize,q,status,companyId,sort`; indexes to support it.
- **PBI 18.3** Lead detail: show company, status history placeholder, edit/delete; bulk-select (for Sprint 3 enrollment).
- **PBI 18.4** Targeted Angular tests (wizard mapping auto-guess logic).

**Sprint 2 demo / exit:** upload a 5–10k row messy CSV on Azure; duplicates/invalids reported per row; leads searchable and filterable; re-uploading the same file imports zero new rows.

---

## 4. Sprint 3 — Campaigns & sending (Weeks 8–9)

### Feature 19 — Campaign Domain & Builder API (branch: `feature/campaigns-backend`)
- **PBI 19.1** Entities: `Campaign` (Name, Status `Draft|Active|Paused|Completed`, FromSenderId, daily send cap, send window/timezone), `CampaignStep` (Order, DelayDays, SubjectTemplate, BodyTemplate, UseAiPersonalisation), `CampaignEnrollment` (CampaignId, LeadId, CurrentStep, NextSendAt, Status `Active|Paused|Completed|Replied|Unsubscribed|Bounced|Failed`), unique `(CampaignId, LeadId)`.
- **PBI 19.2** Scheduling logic in **Domain** (pure, heavily tested): `NextSendAt` computation from delay days respecting workspace time zone + send window; step advancement; completion after last step; stop conditions (reply, unsubscribe, bounce).
- **PBI 19.3** Endpoints: `GET/POST /campaigns`, `PUT /campaigns/{id}`, `POST /campaigns/{id}/steps` (+ reorder/delete), `POST /campaigns/{id}/enroll` (lead ids or filter; skips Unsubscribed/already enrolled/invalid), `POST /campaigns/{id}/activate|pause`.
- **PBI 19.4** Activation validation: ≥1 step, sender verified, template tokens resolvable, steps ordered; steps immutable-ish once active (edits affect only not-yet-sent enrollments — document the rule).
- **PBI 19.5** Template rendering (`{{token}}` replace, missing-token fallback/blocking rule) + **unsubscribe link** token injected into every email (required for deliverability/compliance).

### Feature 20 — Sending Engine (branch: `feature/sendgrid-sending-engine`)
- **PBI 20.1** `EmailMessage` entity (WorkspaceId, EnrollmentId, StepId, SendGridMessageId, Subject, Body, Status, QueuedAt, SentAt, OpenedAt, BouncedAt, Error) and `IEmailSender.SendCampaignEmailAsync` (custom args carry `emailMessageId`/`workspaceId` for webhook correlation; `Message-ID`/`In-Reply-To` headers for threading; **Reply-To = inbound subdomain address encoding the thread/enrollment id** — ties into Sprint 4).
- **PBI 20.2** Hangfire recurring job (every 1–5 min): query due active enrollments (`NextSendAt <= now`), process in batches, per-workspace daily cap + per-minute throttle, idempotent send (see §1), optional AI personalisation call per step (log to `AiGenerationLog`), advance enrollment, mark Completed after last step. Failures: retry with backoff, then mark enrollment `Failed` with reason; never block other enrollments.
- **PBI 20.3** `AiGenerationLog` table + per-workspace monthly soft cap (parent §9): every OpenAI call (compose, personalisation, later classification) writes tokens/cost; cap check before call; friendly error + UI banner when exceeded. Migrate Phase 1 `ILogger`-only usage into this.
- **PBI 20.4** Unsubscribe endpoint (public, token-signed) → sets lead `Unsubscribed`, pauses enrollments; global per-workspace suppression check before every send.
- **PBI 20.5** Tests: scheduling across time zones/DST, send-window edge cases, idempotency under job retry (simulate crash between SendGrid call and DB commit → documented behavior), cap enforcement, suppression, fake `IEmailSender`.

### Feature 21 — Campaigns UI (branch: `feature/angular-campaigns`)
- **PBI 21.1** Campaign list (status chips, enrolled/sent counts), create/edit.
- **PBI 21.2** Step builder: add/reorder/delete steps, delay days, subject/body editor with token insert helper, **"Preview with lead"** and **"Generate with AI"** (reuses compose endpoint), display of resulting send timeline (e.g. "Day 0, Day 3, Day 7") — unit test the schedule-display logic.
- **PBI 21.3** Enrollment: select leads (from leads list bulk-select or "all matching filter"), preview skipped counts, confirm; enrollment view table (lead, current step, next send, status).
- **PBI 21.4** Activate/pause controls with validation errors surfaced; sender-domain status indicator (verified/not) linking to setup help.

**Sprint 3 demo / exit:** a 3-step campaign activated on Azure sends step 1 to enrolled test leads via SendGrid (real inbox received), follow-ups fire on schedule (shorten delays to minutes in a test campaign), unsubscribe link works, AI cost rows visible.

---

## 5. Sprint 4 — Unified inbox (Weeks 10–11)

### Feature 22 — Inbound Parse Webhook & Threading (branch: `feature/inbound-webhook`)
- **PBI 22.1** Entities: `InboxThread` (WorkspaceId, LeadId, CampaignId?, Subject, LastMessageAt, Status/IsRead), `InboxMessage` (ThreadId, Direction `Inbound|Outbound`, From, To, Subject, Body (text + sanitized html), ReceivedAt/SentAt, Classification, ClassificationConfidence, RawMessageId). Outbound campaign sends create/attach to a thread.
- **PBI 22.2** `POST /api/v1/webhooks/sendgrid/inbound` (anonymous, secret-in-URL verified, size-limited): parse multipart, resolve workspace/thread via Reply-To encoding → fallback to `In-Reply-To`/References header → fallback to sender email match; unmatched → quarantine log, never dropped silently. Strip quoted reply text/signatures best-effort; **sanitize HTML** (store text primarily; escape on render).
- **PBI 22.3** Idempotency on `Message-ID`; reject auto-replies/OOO/bounce-DSNs from being classified as human replies (header heuristics: `Auto-Submitted`, `X-Autoreply`, `Precedence`).
- **PBI 22.4** On inbound: lead → `Replied`, enrollment → `Replied` (sequence stops) *unless* classification says otherwise (OOO).
- **PBI 22.5** Tests with captured SendGrid payload fixtures (multipart), threading fallbacks, duplicate delivery, spoofed/unknown sender, cross-tenant routing.

### Feature 23 — AI Reply Classification (branch: `feature/reply-classification`)
- **PBI 23.1** `IReplyClassifier` (Application) → Semantic Kernel impl; structured JSON `{ classification: interested|not_interested|unsubscribe, confidence }`. **Deserialize with `JsonSerializerDefaults.Web`, validate required fields/enum values explicitly, camelCase fixtures in tests** (parent plan risk §11, Phase 1 Feature 6 finding). Unknown/invalid → `Unclassified` + log, never throw into the webhook path.
- **PBI 23.2** Classification runs in a Hangfire job (not inline in the webhook, so SendGrid gets a fast 200); logged to `AiGenerationLog` and subject to workspace cap (over cap → `Unclassified`, still shown in inbox).
- **PBI 23.3** `unsubscribe` classification auto-pauses enrollment, sets lead `Unsubscribed`, adds to suppression (parent §4 Sprint 4). Low-confidence → no auto-action, flagged for manual review.
- **PBI 23.4** Prompt-injection guard: reply text delivered in a delimited block, classifier instructed to ignore instructions inside it; output constrained to the enum. Test with adversarial reply fixtures.
- **PBI 23.5** Tests with fake `IReplyClassifier`; prompt-builder tests.

### Feature 24 — Inbox API & UI (branch: `feature/inbox-ui`)
- **PBI 24.1** `GET /inbox/threads` (paging, filter by classification/unread/campaign), `GET /inbox/threads/{id}` (marks read), `POST /inbox/threads/{id}/reply` (sends via `IEmailSender` with proper threading headers, records outbound `InboxMessage`), manual classification override.
- **PBI 24.2** Angular: thread list (classification chips, unread state, lead/campaign), conversation view, reply composer (optional "AI suggest reply" via compose endpoint — stretch), manual reclassify. Polling every ~30s for new messages (SignalR is out of scope).
- **PBI 24.3** Add `/api/v1/inbox` + `/api/v1/webhooks` to `proxy.conf.js`.

**Sprint 4 demo / exit:** reply to a campaign email from a real mailbox → appears in the inbox within ~1 minute, threaded to the right lead, classified; "unsubscribe" reply auto-pauses the sequence; Owner can answer from the inbox.

---

## 6. Sprint 5 — Analytics, UAT, hardening (Weeks 12–13)

### Feature 25 — SendGrid Event Webhook & Analytics (branch: `feature/analytics`)
- **PBI 25.1** `POST /api/v1/webhooks/sendgrid/events` — **verify ECDSA signature + timestamp** before processing, 401 otherwise; correlate via custom args → update `EmailMessage` (`delivered`, `open`, `bounce`/`dropped`, `spamreport`); idempotent on `sg_event_id`; bounce → enrollment `Bounced` + lead flagged; spam report → suppress.
- **PBI 25.2** `GET /analytics/campaigns/{id}/summary`, `GET /analytics/workspace/summary`: sent, delivered, opened (unique), replied, bounced + rates; date-range filter; computed with indexed aggregate queries (add indexes on `EmailMessage(WorkspaceId, CampaignId, SentAt)`).
- **PBI 25.3** Angular `features/analytics`: workspace dashboard + per-campaign view (stat tiles, per-step breakdown, trend line); caveat tooltip on open rate (MPP). Follow the project's dataviz conventions for charts.
- **PBI 25.4** Tests: signature verification (valid/invalid/replayed), event idempotency/out-of-order, rate math (zero-division, unique opens).

### Feature 26 — Observability, Security & Reliability Hardening (branch: `feature/mvp-hardening`)
- **PBI 26.1** Serilog end-to-end into Application Insights with workspace/user enrichers (no PII/email bodies in logs); alerts: failed Hangfire jobs, 5xx rate, webhook failures, daily send failures.
- **PBI 26.2** Security pass: rate-limiting on auth + public endpoints (built-in rate limiter), account lockout, password policy, security headers/CSP for SPA, upload validation, webhook auth review, dependency audit (`dotnet list package --vulnerable`, `npm audit`), confirm no secrets in repo/history.
- **PBI 26.3** Tenant isolation **integration suite** over *every* endpoint added since Sprint 1 (parent plan risk §11: required before Gate 2 demo); verify no `IgnoreQueryFilters` outside allow-list.
- **PBI 26.4** Data safety: Azure SQL automated backup/PITR verified with a test restore; migration rollback notes; Blob lifecycle rule (delete CSVs after 30 days).
- **PBI 26.5** Performance sanity: leads list at 50k rows, sender job at 1k due enrollments, inbox thread list; fix N+1s/indexes found.
- **PBI 26.6** Error/empty/loading states audit across all Angular screens; expired-session behavior; accessibility basics.

### Feature 27 — UAT & Gate 2 Readiness (branch: `chore/uat-gate2`)
- **PBI 27.1** UAT script = Gate 2 loop: register/invite → import CSV → build campaign (with AI step) → activate → receive email → reply → see classified thread → read analytics. ≥3 internal users each run it on **production** Azure with their own workspace/mailbox; log bugs in a UAT sheet (`docs/uat-results.md`), triage P0/P1 fixed in-sprint, rest to backlog.
- **PBI 27.2** Smoke path written as a one-page checklist re-run before each release (parent plan §9).
- **PBI 27.3** Cost audit: Azure + SendGrid + OpenAI actuals vs. $105–175/month; project to steady state.
- **PBI 27.4** Gate 2 rehearsal on a clean workspace, then live demo; record decision and Phase 3 backlog.

---

## 7. Gate 2 exit criteria (from parent plan §4 — checklist)

- [ ] MVP deployed to Azure and reachable outside the local network (custom domain, HTTPS)
- [ ] ≥3 internal users completed: import leads → build campaign → send → see reply in inbox → read analytics
- [ ] Infrastructure spend tracking to the $105–175/month band
- [ ] (Internal, from risk table) Tenant isolation suite green before the demo

---

## 8. Feature dependency & parallelism map

```
Sprint 1:  F11 (Azure/CI) ──────────────┐
           F12 (tenant filter) ─► F13 (members/invites API) ─► F14 (members UI)
           F15 (SendGrid DNS) — start day 1, runs in background all sprint
Sprint 2:  F16 (Company/Lead) ─► F17 (CSV backend) ─► F18 (leads UI)
Sprint 3:  F19 (campaign domain/API) ─► F20 (sending engine) ─► F21 (campaigns UI; UI can start against F19 API mid-sprint)
Sprint 4:  F22 (inbound webhook) ─► F23 (classification) ─► F24 (inbox UI; UI can start once F22 entities exist)
Sprint 5:  F25 (events + analytics) ∥ F26 (hardening) ─► F27 (UAT / Gate 2)
```
- F12 **must** precede every later entity; F11 should land first so every later merge is auto-deployed.
- With 3 devs: Backend = F12/F13/F17/F19/F20/F22/F23/F25; Frontend = F14/F18/F21/F24/F25-UI; DevOps/full-stack = F11/F15/F26/F27 plus tenant-isolation tests. With 2 devs, the backend dev also owns F11/F15 and F26 slips toward UI-light polish — protect F12 and F26.3 from being cut.

---

## 9. Data model delta (Phase 1 → end of Phase 2)

New tables: `WorkspaceMember`, `WorkspaceInvite`, `Company`, `CsvImportBatch`, `Campaign`, `CampaignStep`, `CampaignEnrollment`, `EmailMessage`, `InboxThread`, `InboxMessage`, `AiGenerationLog`, plus Hangfire's own schema.
Changed: `Workspace` (+PlanTier, TimeZone, Country), `Lead` (+CompanyId, unique `(WorkspaceId, Email)`, soft delete), `AppUser` (WorkspaceId becomes default-workspace pointer).
All tenant tables implement `ITenantEntity` and carry the global query filter; `RefreshToken`, Identity tables, and Hangfire tables are the only unfiltered ones.

## 10. API surface delta (all `/api/v1`)

| Area | New in Phase 2 |
|---|---|
| Auth/workspace | `POST /auth/accept-invite`, `POST /workspaces/{id}/invite`, `GET /workspaces/{id}/members`, revoke invite |
| Leads | `PUT/DELETE /leads/{id}`, `POST /leads/import`, `POST /leads/import/{batchId}/start`, `GET /leads/import/{batchId}/status`, `GET .../errors`; query params on `GET /leads` |
| Campaigns | `GET/POST /campaigns`, `PUT /campaigns/{id}`, steps CRUD, `POST /campaigns/{id}/enroll|activate|pause`, enrollments list |
| Inbox | `GET /inbox/threads`, `GET /inbox/threads/{id}`, `POST /inbox/threads/{id}/reply`, reclassify |
| Analytics | `GET /analytics/campaigns/{id}/summary`, `GET /analytics/workspace/summary` |
| Public/webhooks | `GET /unsubscribe/{token}`, `POST /webhooks/sendgrid/inbound`, `POST /webhooks/sendgrid/events` |
| Ops | `GET /health`, `/hangfire` (Owner only) |

## 11. Package additions

| Project | New packages |
|---|---|
| `ZenLead.Infrastructure` | `Hangfire.Core`, `Hangfire.SqlServer`, `SendGrid`, `Azure.Storage.Blobs`, `Azure.Identity`, `CsvHelper`, `Serilog.*` sinks (Application Insights) |
| `ZenLead.Api` | `Hangfire.AspNetCore`, `Azure.Extensions.AspNetCore.Configuration.Secrets`, `Serilog.AspNetCore`, `Microsoft.ApplicationInsights.AspNetCore`, `AspNetCore.HealthChecks.*` (optional) |
| `ZenLead.Application` | none expected (interfaces only) |
| `ZenLead.Tests` | Testcontainers (SQL Server) or a LocalDB fixture; `Microsoft.AspNetCore.Mvc.Testing` for endpoint/tenant tests |
| `ZenLead.Client` | chart library chosen per dataviz guidance (e.g. `ngx-charts`/`chart.js`) — decide in Feature 25 |

## 12. Testing priorities (per CLAUDE.md conventions)

Must-have xUnit coverage: tenant filter on every entity; CSV parse/dedup; sequence scheduling (time zones/DST/send window); send idempotency; invite lifecycle; webhook signature verification and idempotency; structured-AI-output parsing (Web defaults + camelCase fixtures). AI and SendGrid always behind fakes — no real calls in CI. Angular: auth interceptor (existing), import-wizard mapping guess, step schedule display. Manual smoke path before each release.

## 13. Phase 2 risks & mitigations (additions to parent plan §11)

| Risk | Mitigation |
|---|---|
| DNS/MX propagation or SendGrid domain verification delays | Feature 15 in week 4; keep a fallback of testing with SendGrid single-sender verification so Sprint 3 isn't blocked |
| New domain/IP has poor deliverability; test emails land in spam | Warm-up volume caps per workspace, SPF/DKIM/DMARC set, unsubscribe link, plain-text part; internal UAT uses small volumes |
| Double-sends from Hangfire retries/overlapping runs | Insert-before-send + `DisableConcurrentExecution` on the sender job + DB-level unique constraint on `(EnrollmentId, StepId)` for `EmailMessage` |
| Global filter breaks background jobs/webhooks (no JWT) | Explicit workspace scope service for non-HTTP contexts, covered by tests in Features 12/17/20/22 |
| Inbound reply can't be matched to a thread | Three-level matching (Reply-To token → headers → sender email) + quarantine view; never silently drop |
| Prompt injection via reply text/CSV fields feeding the LLM | Delimited input, enum-constrained output, no tool access, adversarial fixtures |
| AI cost overruns with per-step personalisation across thousands of leads | Workspace cap enforced before each call; personalisation optional per step; show estimated cost on activate |
| Azure cost drift above $175/month | Budget alert (F11.5), scale-down options documented (S0 SQL, B1 plan), weekly check in UAT |
| 2-dev capacity: 5 sprints is tight | Stretch items (AI reply suggestion, soft-delete UI, charts polish) are first to cut; F12, F20, F22, F26.3 are not |
| CSV scale: large files time out/OOM | Streaming parse + batched commits + file/row caps from day one |

## 14. Explicitly out of scope for Phase 2 (don't build yet)

LinkedIn/WhatsApp/SMS; CRM pipeline/deal stages; Stripe billing/plan enforcement; white-label/custom domains/PDF reports; granular RBAC beyond Owner/Member; GDPR/PDPL tooling (beyond unsubscribe/suppression); lead scoring beyond reply classification; CRM integrations/public API; multi-workspace switching UI; SignalR/real-time inbox; branching sequences; A/B testing; calendar/meeting booking; email warm-up automation.

---

*Scoped to Proposal Phase 2, weeks 4–13 of [zen-lead-mvp-implementation-plan.md](zen-lead-mvp-implementation-plan.md). Update §0 "Starting state" and CLAUDE.md "Project state" as each sprint lands.*
