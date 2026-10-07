# ZenLead — Phase 2 (Minimum Viable Product) Implementation Plan
**Scope: Weeks 4–13 of [zen-lead-mvp-implementation-plan.md](zen-lead-mvp-implementation-plan.md) (§4 Phase 2) only.** Five two-week sprints, deployed to Azure throughout. Goal: clear **Gate 2** — MVP live on Azure, used internally by Zenrax (owner + up to 2 colleagues, **one shared workspace**) to complete import → campaign → send → reply → analytics.

> **Scope decision (single-organisation MVP):** until further notice only Zenrax uses ZenLead. Multi-tenancy (tenant query filter, members/roles/invites, per-workspace isolation suite) is **deferred to a later phase** (see §14). `WorkspaceId` stays on every table so it can be switched on later without a data migration. Cost target is re-baselined for this scale (see §1a).

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
- [x] Domain: `zenraxconsultancy.com` already owned (GoDaddy DNS). App + sending identity live on `leads.zenraxconsultancy.com`; inbound replies on `reply.leads.zenraxconsultancy.com` (see §1b).
- [ ] Confirm team size (2 vs 3 devs) — drives the parallelism in §6.

---

## 1. Decisions to lock before coding

| Decision | Choice | Why |
|---|---|---|
| Background jobs | **Hangfire, in-process, SQL Server storage** (same Azure SQL DB) | Parent plan §1; no extra infra. Dashboard behind auth (allow-listed users). |
| Tenancy | **Single workspace (Zenrax); multi-tenancy deferred.** Every new tenant-owned table still carries `WorkspaceId` (from `ICurrentWorkspace`, see F12). No global query filter, no members/roles/invites in Phase 2. | Only Zenrax uses the app. Keeping the column makes the later filter + isolation suite additive, not a data migration. |
| Access control | Registration **closed except for an allow-list** (`Auth:AllowedEmails`, per-environment config); allowed users join the single default workspace. Hangfire dashboard requires an authenticated allowed user. | Replaces invites/roles: 1–3 trusted users share one workspace. |
| Email provider | **SendGrid** (v3 send API, Inbound Parse, Event Webhook) behind `IEmailSender` in Application | Same swappable-interface rule as `IEmailComposer`. |
| Webhook auth | SendGrid **signed event webhook** (ECDSA public key verify) for events; shared secret in the Inbound Parse URL for inbound (Parse has no signing) | Webhooks are unauthenticated by JWT; must be verified before any processing. |
| Secrets | Azure Key Vault via managed identity; `user-secrets` stays for local only | Parent plan §7/§9. Costs cents/month at this scale (§1a); keeps secrets out of app settings and deploy config. |
| Logging | Serilog → Application Insights sink from the first deploy | Parent plan §9. |
| Blob staging | CSV uploaded to Azure Blob (`imports/{workspaceId}/{batchId}.csv`) behind `IBlobStorage`, parsed by a Hangfire job; Azurite locally | Keeps large uploads off request threads. |
| Template syntax | Simple `{{firstName}}`, `{{company}}`, `{{title}}` token replacement (no Liquid/Razor) | Enough for MVP; avoids template-injection surface. |
| Email send idempotency | `EmailMessage` row inserted (status `Queued`) **before** calling SendGrid; enrollment advanced in the same transaction as the sent-marking | Prevents double-sends on Hangfire retry. |
| Open tracking | SendGrid open pixel via Event Webhook; treat opens as approximate (Apple MPP) | Report honestly in analytics UI. |
| API versioning/proxy | All new routes under `/api/v1/...`; **add each new path prefix to `ZenLead.Client/src/proxy.conf.js`** | Otherwise `ng serve` 404s. |
| Migrations in prod | Applied by a CI/CD step (`dotnet ef migrations bundle`), never on app start in prod | Avoid slot-swap races and surprise schema changes. |

Note: parent plan §6 lists `POST /ai/compose-email` as `/api/v1`; Phase 1 already followed that.

### 1a. Infrastructure cost (single-organisation baseline)

Prices are USD, list price, US regions, checked 2026-10-07. Rows marked *est.* are not from a source I checked — verify in the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) before committing. Re-check at Sprint 1 kickoff and in the PBI 27.3 cost audit.

| Resource | Original plan | Single-org choice | Approx. monthly | Source / note |
|---|---|---|---|---|
| App Service plan (Linux) | B1/S1 + `staging` slot (slots need S1+; S1 ≈ $69 *est.*) | **B1**, no slot | **$13.14** | [Azure App Service pricing](https://azure.microsoft.com/pricing/details/app-service/). Don't use Free/F1: no always-on, CPU quota — breaks the in-process Hangfire sender job. |
| Azure SQL | Basic/S0 (S0 ≈ $15 *est.*) | **Free serverless offer** (100k vCore-s, 32 GB/month), fallback **Basic** | **$0** (fallback $5) | [Free offer](https://learn.microsoft.com/azure/azure-sql/database/free-offer), Basic $5 for 5 DTU/2 GB. Hangfire polling may keep the DB awake and burn the free vCore-seconds; if it does, switch to Basic. Basic's 2 GB cap is fine for ~50k leads but watch it. |
| Key Vault (Standard) | Yes | **Yes**, access via managed identity | **< $0.01** | ~$0.03 per 10k operations *est.*; a handful of reads at startup. Don't use Premium (HSM). |
| Storage account / Blob | Yes | **Yes**, LRS, Hot, private, 30-day lifecycle rule | **$0.05–0.50** *est.* | ~$0.018/GB/month *est.*; CSVs ≤ 10 MB deleted after 30 days. Also protects imports from App Service local-disk loss on restart/redeploy. |
| App Insights / Log Analytics | Yes | Yes, **daily ingestion cap** | $0–3 *est.* | First 5 GB/month ingestion free *(from memory)*; the cap prevents surprise bills. |
| Domain | ~$15/yr | Already owned (`zenraxconsultancy.com`, GoDaddy) | $0 | Subdomains are free; see §1b for DNS layout. |
| **Azure subtotal** | ≈ $100+ *est.* with slot/S1 | | **≈ $15–25** | Budget alert at **$40** leaves headroom. |
| SendGrid | Essentials | Cheapest plan that supports Inbound Parse + Event Webhook | **from $19.95** | The permanent free plan is gone; only a 60-day, 100/day trial remains ([pricing overview](https://automationatlas.io/answers/sendgrid-pricing-explained-2026/)). Amazon SES (~$0.10/1k *est.*) would be cheaper, but `IEmailSender` has no inbound-parse equivalent there — a swap means rebuilding Feature 22. Revisit only if SendGrid becomes the main line item. |
| OpenAI | GPT-4o | GPT-4o for compose; **evaluate GPT-4o-mini** for classification/personalisation | usage-based | GPT-4o $2.50/$10 per 1M in/out tokens; GPT-4o-mini $0.15/$0.60 ([source](https://www.analyticsvidhya.com/blog/2024/12/openai-api-cost/)). Rough: 1,000 calls at ~500 in / 300 out tokens ≈ $4.25 on GPT-4o, ≈ $0.26 on mini. Keep the per-environment spending cap. |
| **Realistic total** | $105–175 | | **≈ $35–50 + OpenAI usage** | Dominated by SendGrid. |

**Budget alerts:** Azure Cost Management at $40/month (Azure only); OpenAI hard cap set in the OpenAI dashboard.

**First upgrades if usage grows** (in order): Azure SQL Basic → S0; B1 → S1 and add the staging slot once there are customers who'd notice a bad deploy; Premium-tier or additional vaults only if compliance requires it.

### 1b. Domain & DNS layout (`zenraxconsultancy.com`, GoDaddy)

| Purpose | Host (GoDaddy "Name" field = the part before `.zenraxconsultancy.com`) | Type | Value |
|---|---|---|---|
| App (App Service custom domain) | `leads` | CNAME | `<app>.azurewebsites.net` |
| App domain ownership | `asuid.leads` | TXT | verification id shown by App Service |
| SendGrid domain authentication (DKIM/return-path) | records SendGrid generates for `leads.zenraxconsultancy.com` (e.g. `s1._domainkey.leads`, `em1234.leads`) | CNAME | per SendGrid |
| Inbound replies (Inbound Parse) | `reply.leads` | MX (priority 10) | `mx.sendgrid.net` |

Rules: a name with a CNAME (`leads`) cannot also hold MX/TXT, so inbound uses the **`reply.leads`** child name, not `leads` itself. MX on a subdomain does not affect the root domain's mail (Microsoft 365/Google/etc.). App Service managed certificates are free on B1 and above, so HTTPS on `leads.` costs nothing. Using a dedicated subdomain isolates cold-outreach reputation from the root domain; set DMARC at the root (`_dmarc`) with `sp=` covering subdomains, and do not send outreach from the root domain.

---

## 2. Sprint 1 — Foundation (Weeks 4–5)

Goal: app running on Azure via CI/CD; single shared workspace with allow-listed access; DNS started for Sprint 4.

### Feature 11 — Azure Provisioning & CI/CD (branch: `feature/azure-cicd`)
- **PBI 11.1 — Infra provisioning** (Bicep or `az` scripts committed under `infra/` so it's reproducible): resource group, App Service plan (**B1 Linux**, no staging slot) + app, Azure SQL (Basic, or the serverless free offer if Hangfire polling doesn't defeat auto-pause), Application Insights/Log Analytics with a daily ingestion cap. Storage account (Blob, LRS/Hot/private, lifecycle rule deleting `imports/` blobs after 30 days) and Key Vault (Standard). App Service **managed identity** gets Key Vault `get/list` and `Storage Blob Data Contributor` on the storage account (no keys or connection strings in settings).
- **PBI 11.2 — Config & secrets**: move `Jwt:*`, `ConnectionStrings:Default`, `OpenAI:ApiKey` into Key Vault (`AddAzureKeyVault` with `DefaultAzureCredential`); App Service settings hold only the vault URI. Startup fails fast if a required secret is missing.
- **PBI 11.3 — GitHub Actions pipeline**: on PR → build + `dotnet test` + `ng test`; on merge to `main` → `dotnet publish` (triggers `ng build` into `wwwroot`) → run EF migrations bundle against Azure SQL → deploy to the App Service → smoke check (`/health`); a failed smoke check fails the pipeline (no slot swap at this scale). Branch protection requiring green CI.
- **PBI 11.4 — Health & observability baseline**: `/health` endpoint (DB reachable), Serilog + Application Insights sink, request correlation id, CORS/HTTPS/HSTS settings reviewed.
- **PBI 11.5 — Cost tracking**: Azure Cost Management budget alert (Azure alert at $40/month; see §1a) and OpenAI per-env cap documented.

### Feature 12 — Single-Workspace Access Control (branch: `feature/single-workspace-access`)
*(Replaces the tenant-isolation hardening feature; that work is deferred, see §14.)* Lands before new tenant-owned tables so they all share one `WorkspaceId` source.
- **PBI 12.1 — `ICurrentWorkspace`** (Application interface; returns the workspace id from the JWT claim in HTTP, and the single default workspace in background/webhook contexts). New entities implement a marker `ITenantEntity { WorkspaceId }` and are stamped on insert in `SaveChanges`. **No query filter yet** — the interface and marker are the seam for adding it later.
- **PBI 12.2 — Allow-listed registration**: `RegisterUseCase` rejects emails not in `Auth:AllowedEmails`; the first allowed user creates the "Zenrax" workspace (or it is seeded by migration), later allowed users join it instead of creating their own.
- **PBI 12.3 — Keep Phase 1 manual `workspace_id` checks** in `LeadsController`/`ComposeEmailUseCase` and their regression test (cheap, already built).
- **PBI 12.4 — Tests**: non-allow-listed email rejected; second allowed user lands in the same workspace and sees the same leads.

### Feature 13 — Transactional Email Sender (branch: `feature/email-sender`)
*(Workspace members/roles/invites and the members UI — former Features 13–14 — are deferred, see §14.)*
- **PBI 13.1 — `IEmailSender` + SendGrid client** with a dev "log to console" fake. (Campaign sending in Feature 20 reuses this interface.)
- **PBI 13.2 — Workspace fields**: extend `Workspace` with `TimeZone` (needed for send windows); `PlanTier`/`Country` deferred.
- **PBI 13.3 — Tests**: fake sender, SendGrid payload mapping.

### Feature 14 — App Shell (branch: `feature/angular-shell`)
- **PBI 14.1** Material sidenav + toolbar with nav to Leads/Campaigns/Inbox/Analytics (stubs for later sprints), workspace name, logout. No role-aware UI.

### Feature 15 — SendGrid Domain & Inbound DNS Kickoff (branch: `chore/sendgrid-dns` — mostly non-code)
- **PBI 15.1** SendGrid account, API key (Key Vault), domain authentication (SPF/DKIM CNAMEs) for `leads.zenraxconsultancy.com` (records added at GoDaddy; see §1b).
- **PBI 15.2** Create inbound subdomain (`reply.leads.zenraxconsultancy.com`), **MX record → `mx.sendgrid.net`**, configure Inbound Parse destination URL placeholder. Record DNS TTL/propagation status in `docs/`. *Do this in week 4; Sprint 4 depends on it.*
- **PBI 15.3** Decide and document sender identity model (one verified sender for Zenrax; per-workspace senders deferred) — needed for `Campaign.FromSenderId`.

**Sprint 1 demo / exit:** merge to `main` deploys to Azure automatically; an allow-listed second user registers and sees the same workspace's leads while a non-listed email is rejected; DNS records created and propagating.

---

## 3. Sprint 2 — Leads at volume (Weeks 6–7)

### Feature 16 — Company Entity & Lead Model Extension (branch: `feature/company-lead-extension`)
- **PBI 16.1** `Company` (WorkspaceId, Name, Domain, Industry, Country); `Lead.CompanyId` nullable FK; unique index `(WorkspaceId, Email)` on `Lead` (normalised lowercase) — **check for existing duplicates in the migration** and handle them (merge or fail loudly).
- **PBI 16.2** Lead CRUD completion: `PUT /leads/{id}`, `DELETE /leads/{id}` (soft delete preferred so campaign history survives), `Status` transitions rules in Domain.
- **PBI 16.3** `ComposeEmailUseCase` now pulls `Company` data instead of free-text-only context (keep `context?` as an override).

### Feature 17 — CSV Import Backend (branch: `feature/csv-import-backend`)
Highest-risk data-quality feature; test-first.
- **PBI 17.1 — Domain/persistence**: `CsvImportBatch` (FileName, BlobPath, ColumnMapping json, RowCount, ImportedCount, SkippedDuplicateCount, ErrorCount, ErrorLog, Status `Uploaded|Parsing|Completed|Failed`, CreatedBy).
- **PBI 17.2 — Upload & preview**: `POST /api/v1/leads/import` (multipart; size limit e.g. 10 MB / 50k rows, `.csv` only) → store via `IBlobStorage` in Application (Azure Blob impl using managed identity; Azurite locally) → return `batchId` + header row + first N sample rows for the mapping UI.
- **PBI 17.3 — Mapping & start**: `POST /api/v1/leads/import/{batchId}/start` with column mapping (name, email, title, company name/domain/industry/country); validate mapping covers required `email`.
- **PBI 17.4 — Parser** (`ZenLead.Domain`/`Application`, pure & unit-tested): streaming CSV read (CsvHelper), BOM/encoding/delimiter handling, trim, email syntax validation + lowercase, in-file duplicate detection, DB duplicate detection (batched `WHERE Email IN (...)`), company upsert by domain/name. Per-row result: `Imported | Duplicate | Invalid(reason)`.
- **PBI 17.5 — Hangfire job** `ProcessCsvImportJob(batchId, workspaceId)`: idempotent (resumable/rerunnable without duplicating leads), batches of ~500 rows per transaction, updates batch counters, writes error log (capped, with row numbers), sets final status. Takes `workspaceId` explicitly (no HTTP context).
- **PBI 17.6 — Status & errors**: `GET /api/v1/leads/import/{batchId}/status` (progress, counts), `GET .../errors` (downloadable CSV of rejected rows).
- **PBI 17.7 — Hangfire setup**: SQL storage, auth-protected dashboard (allow-listed users) at `/hangfire`, retry policy (3 attempts, exponential), dead-letter visibility.
- **PBI 17.8 — Tests (priority)**: malformed quotes, empty file, header-only, missing email column, unicode names, 10k-row perf sanity, in-file dupes, existing-DB dupes, case-differing emails, job re-run idempotence, dedup is per workspace (`(WorkspaceId, Email)` index kept).

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
- **PBI 22.5** Tests with captured SendGrid payload fixtures (multipart), threading fallbacks, duplicate delivery, spoofed/unknown sender, unknown-sender routing.

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

**Sprint 4 demo / exit:** reply to a campaign email from a real mailbox → appears in the inbox within ~1 minute, threaded to the right lead, classified; "unsubscribe" reply auto-pauses the sequence; a user can answer from the inbox.

---

## 6. Sprint 5 — Analytics, UAT, hardening (Weeks 12–13)

### Feature 25 — SendGrid Event Webhook & Analytics (branch: `feature/analytics`)
- **PBI 25.1** `POST /api/v1/webhooks/sendgrid/events` — **verify ECDSA signature + timestamp** before processing, 401 otherwise; correlate via custom args → update `EmailMessage` (`delivered`, `open`, `bounce`/`dropped`, `spamreport`); idempotent on `sg_event_id`; bounce → enrollment `Bounced` + lead flagged; spam report → suppress.
- **PBI 25.2** `GET /analytics/campaigns/{id}/summary`, `GET /analytics/workspace/summary`: sent, delivered, opened (unique), replied, bounced + rates; date-range filter; computed with indexed aggregate queries (add indexes on `EmailMessage(WorkspaceId, CampaignId, SentAt)`).
- **PBI 25.3** Angular `features/analytics`: workspace dashboard + per-campaign view (stat tiles, per-step breakdown, trend line); caveat tooltip on open rate (MPP). Follow the project's dataviz conventions for charts.
- **PBI 25.4** Tests: signature verification (valid/invalid/replayed), event idempotency/out-of-order, rate math (zero-division, unique opens).

### Feature 26 — Observability, Security & Reliability Hardening (branch: `feature/mvp-hardening`)
- **PBI 26.1** Serilog end-to-end into Application Insights with user enrichers (no PII/email bodies in logs); alerts: failed Hangfire jobs, 5xx rate, webhook failures, daily send failures.
- **PBI 26.2** Security pass: rate-limiting on auth + public endpoints (built-in rate limiter), account lockout, password policy, security headers/CSP for SPA, upload validation, webhook auth review, dependency audit (`dotnet list package --vulnerable`, `npm audit`), confirm no secrets in repo/history.
- **PBI 26.3** Authorization sweep: every endpoint added since Sprint 1 requires auth (except unsubscribe/webhooks, which have their own token/signature checks) — covered by an integration test. Full tenant-isolation suite deferred with multi-tenancy.
- **PBI 26.4** Data safety: Azure SQL automated backup/PITR verified with a test restore; migration rollback notes; verify the Blob lifecycle rule (delete `imports/` after 30 days) actually removes old CSVs.
- **PBI 26.5** Performance sanity: leads list at 50k rows, sender job at 1k due enrollments, inbox thread list; fix N+1s/indexes found.
- **PBI 26.6** Error/empty/loading states audit across all Angular screens; expired-session behavior; accessibility basics.

### Feature 27 — UAT & Gate 2 Readiness (branch: `chore/uat-gate2`)
- **PBI 27.1** UAT script = Gate 2 loop: register (allow-listed) → import CSV → build campaign (with AI step) → activate → receive email → reply → see classified thread → read analytics. ≥2 Zenrax users (owner + one colleague) each run it on **production** Azure with their own mailbox in the shared workspace; log bugs in a UAT sheet (`docs/uat-results.md`), triage P0/P1 fixed in-sprint, rest to backlog.
- **PBI 27.2** Smoke path written as a one-page checklist re-run before each release (parent plan §9).
- **PBI 27.3** Cost audit: Azure + SendGrid + OpenAI actuals vs. the re-baselined target (see §1a: ≈ $35–50/month incl. SendGrid, plus OpenAI usage); project to steady state.
- **PBI 27.4** Gate 2 rehearsal on a clean workspace, then live demo; record decision and Phase 3 backlog.

---

## 7. Gate 2 exit criteria (from parent plan §4 — checklist)

- [ ] MVP deployed to Azure and reachable outside the local network (custom domain, HTTPS)
- [ ] ≥2 Zenrax users (shared workspace) completed: import leads → build campaign → send → see reply in inbox → read analytics
- [ ] Infrastructure spend within the re-baselined single-org budget (§1a: ≈ $35–50/month incl. SendGrid, plus OpenAI usage; the original $105–175 band no longer applies)
- [ ] (Internal) Non-allow-listed registration rejected; all non-public endpoints require auth

---

## 8. Feature dependency & parallelism map

```
Sprint 1:  F11 (Azure/CI) ──────────────┐
           F12 (single-workspace access) ─► F13 (email sender) ─► F14 (app shell)
           F15 (SendGrid DNS) — start day 1, runs in background all sprint
Sprint 2:  F16 (Company/Lead) ─► F17 (CSV backend) ─► F18 (leads UI)
Sprint 3:  F19 (campaign domain/API) ─► F20 (sending engine) ─► F21 (campaigns UI; UI can start against F19 API mid-sprint)
Sprint 4:  F22 (inbound webhook) ─► F23 (classification) ─► F24 (inbox UI; UI can start once F22 entities exist)
Sprint 5:  F25 (events + analytics) ∥ F26 (hardening) ─► F27 (UAT / Gate 2)
```
- F12 should precede later entities (shared `ICurrentWorkspace`/`ITenantEntity` seam); F11 should land first so every later merge is auto-deployed.
- With 3 devs: Backend = F12/F13/F17/F19/F20/F22/F23/F25; Frontend = F14/F18/F21/F24/F25-UI; DevOps/full-stack = F11/F15/F26/F27. With 2 devs, the backend dev also owns F11/F15 and F26 slips toward UI-light polish — protect F20, F22 and F26.3 from being cut.

---

## 9. Data model delta (Phase 1 → end of Phase 2)

New tables: `Company`, `CsvImportBatch`, `Campaign`, `CampaignStep`, `CampaignEnrollment`, `EmailMessage`, `InboxThread`, `InboxMessage`, `AiGenerationLog`, plus Hangfire's own schema.
Changed: `Workspace` (+TimeZone), `Lead` (+CompanyId, unique `(WorkspaceId, Email)`, soft delete). `AppUser.WorkspaceId` unchanged.
All tenant tables implement `ITenantEntity` and carry `WorkspaceId`, but **no global query filter in Phase 2** (deferred).

## 10. API surface delta (all `/api/v1`)

| Area | New in Phase 2 |
|---|---|
| Auth | Existing endpoints; `register` gated by `Auth:AllowedEmails` |
| Leads | `PUT/DELETE /leads/{id}`, `POST /leads/import`, `POST /leads/import/{batchId}/start`, `GET /leads/import/{batchId}/status`, `GET .../errors`; query params on `GET /leads` |
| Campaigns | `GET/POST /campaigns`, `PUT /campaigns/{id}`, steps CRUD, `POST /campaigns/{id}/enroll|activate|pause`, enrollments list |
| Inbox | `GET /inbox/threads`, `GET /inbox/threads/{id}`, `POST /inbox/threads/{id}/reply`, reclassify |
| Analytics | `GET /analytics/campaigns/{id}/summary`, `GET /analytics/workspace/summary` |
| Public/webhooks | `GET /unsubscribe/{token}`, `POST /webhooks/sendgrid/inbound`, `POST /webhooks/sendgrid/events` |
| Ops | `GET /health`, `/hangfire` (allow-listed users) |

## 11. Package additions

| Project | New packages |
|---|---|
| `ZenLead.Infrastructure` | `Hangfire.Core`, `Hangfire.SqlServer`, `SendGrid`, `Azure.Storage.Blobs`, `Azure.Identity`, `CsvHelper`, `Serilog.*` sinks (Application Insights) |
| `ZenLead.Api` | `Hangfire.AspNetCore`, `Azure.Extensions.AspNetCore.Configuration.Secrets`, `Serilog.AspNetCore`, `Microsoft.ApplicationInsights.AspNetCore`, `AspNetCore.HealthChecks.*` (optional) |
| `ZenLead.Application` | none expected (interfaces only) |
| `ZenLead.Tests` | Testcontainers (SQL Server) or a LocalDB fixture; `Microsoft.AspNetCore.Mvc.Testing` for endpoint tests |
| `ZenLead.Client` | chart library chosen per dataviz guidance (e.g. `ngx-charts`/`chart.js`) — decide in Feature 25 |

## 12. Testing priorities (per CLAUDE.md conventions)

Must-have xUnit coverage: CSV parse/dedup; allow-listed registration; sequence scheduling (time zones/DST/send window); send idempotency; webhook signature verification and idempotency; structured-AI-output parsing (Web defaults + camelCase fixtures). AI and SendGrid always behind fakes — no real calls in CI. Angular: auth interceptor (existing), import-wizard mapping guess, step schedule display. Manual smoke path before each release.

## 13. Phase 2 risks & mitigations (additions to parent plan §11)

| Risk | Mitigation |
|---|---|
| DNS/MX propagation or SendGrid domain verification delays | Feature 15 in week 4; keep a fallback of testing with SendGrid single-sender verification so Sprint 3 isn't blocked |
| New domain/IP has poor deliverability; test emails land in spam | Warm-up volume caps per workspace, SPF/DKIM/DMARC set, unsubscribe link, plain-text part; internal UAT uses small volumes |
| Double-sends from Hangfire retries/overlapping runs | Insert-before-send + `DisableConcurrentExecution` on the sender job + DB-level unique constraint on `(EnrollmentId, StepId)` for `EmailMessage` |
| Deferring multi-tenancy makes it costly to add later | Keep `WorkspaceId` on every table, the `ICurrentWorkspace` + `ITenantEntity` seam (F12), and no cross-workspace queries; adding the filter + isolation suite later is then additive |
| Inbound reply can't be matched to a thread | Three-level matching (Reply-To token → headers → sender email) + quarantine view; never silently drop |
| Prompt injection via reply text/CSV fields feeding the LLM | Delimited input, enum-constrained output, no tool access, adversarial fixtures |
| AI cost overruns with per-step personalisation across thousands of leads | Workspace cap enforced before each call; personalisation optional per step; show estimated cost on activate |
| Azure cost drift above single-org budget | Budget alert (F11.5), B1 plan + Basic/free SQL, App Insights ingestion cap, weekly check in UAT |
| Capacity: 5 sprints is tight | Stretch items (AI reply suggestion, soft-delete UI, charts polish) are first to cut; F20, F22, F26.3 are not |
| CSV scale: large files time out/OOM | Streaming parse + batched commits + file/row caps from day one |

## 14. Explicitly out of scope for Phase 2 (don't build yet)

**Deferred multi-tenancy (later phase):** EF Core global tenant query filter; `WorkspaceMember`/roles (`Owner`|`Member`); invites and accept-invite flow; members UI; `PlanTier`/`Country` on `Workspace`; tenant-isolation integration suite; per-workspace sender identity; multiple internal workspaces. The original designs are in this file's git history.

LinkedIn/WhatsApp/SMS; CRM pipeline/deal stages; Stripe billing/plan enforcement; white-label/custom domains/PDF reports; any RBAC; GDPR/PDPL tooling (beyond unsubscribe/suppression); lead scoring beyond reply classification; CRM integrations/public API; multi-workspace switching UI; SignalR/real-time inbox; branching sequences; A/B testing; calendar/meeting booking; email warm-up automation.

---

*Scoped to Proposal Phase 2, weeks 4–13 of [zen-lead-mvp-implementation-plan.md](zen-lead-mvp-implementation-plan.md). Update §0 "Starting state" and CLAUDE.md "Project state" as each sprint lands.*
