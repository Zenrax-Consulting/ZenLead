# ZenLead — Phase 2 (Minimum Viable Product) Implementation Plan
**Scope: Weeks 4–13 of [zen-lead-mvp-implementation-plan.md](zen-lead-mvp-implementation-plan.md) (§4 Phase 2) only.** Five two-week sprints; development and demos run **locally**, and Azure provisioning/CI-CD (Feature 30) is deliberately the last feature, just before UAT. Goal: clear **Gate 2** — MVP live on Azure, used internally by Zenrax (owner + up to 2 colleagues, sharing one **Zenrax workspace**) to complete import → campaign → send → reply → analytics.

> **Scope decision (multi-tenant model, single-organisation use):** until further notice only Zenrax uses ZenLead, but the multi-tenant model from Phase 1 stays and is hardened (`WorkspaceId` on every tenant table, EF global query filter, isolation tests — Feature 11). **Colleagues share one workspace:** registration is by picking an existing workspace from a searchable list, and the workspace's admin (one per workspace, identified by an admin email) approves each new user via an emailed link (Feature 20). Roles beyond admin/member, invites and a user-management UI remain deferred (see §14). Cost target is re-baselined for this scale (see §1a).

Format follows [phase-1-poc-implementation-plan.md](phase-1-poc-implementation-plan.md): numbered features continue from Phase 1 (which ended at Feature 10), each is one feature branch + PR, with PBIs underneath. Per-feature file-by-file docs (like `phase-1-features/`) are in [phase-2-features/](phase-2-features/README.md) (drafted 2026-10-08 for all of Features 11–31; re-check each against the code at the start of its sprint). Where a feature doc deviates from the wording below, the README there lists it under "Deviations from the parent plan".

> **Numbering:** features are numbered 11–31 in build order (renumbered 2026-10-07 after the sprint re-sequencing). Earlier drafts used different numbers; PBI numbers follow their feature.

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
- [x] Domain: `zenraxconsultancy.com` already owned (GoDaddy DNS). App lives on `leads.zenraxconsultancy.com`; sending identity is `info@zenraxconsultancy.com` (root domain, owner decision 2026-10-08); inbound replies on `reply.leads.zenraxconsultancy.com` (see §1b).
- [ ] Confirm team size (2 vs 3 devs) — drives the parallelism in §6.

---

## 1. Decisions to lock before coding

| Decision | Choice | Why |
|---|---|---|
| Background jobs | **Hangfire, in-process, SQL Server storage** (same Azure SQL DB) | Parent plan §1; no extra infra. Dashboard restricted to admin emails in config (`Hangfire:AdminEmails`) — not to any registered user. |
| Tenancy | **Multi-tenant, shared DB/schema**: `WorkspaceId` on every tenant table via `ITenantEntity`, EF global query filter + isolation tests in Feature 11. Several users share a workspace (membership via Feature 20); one admin per workspace (`Workspace.AdminEmail`). Roles beyond admin/member, invites, members UI deferred. | Keeping the model costs little now and avoids a risky retrofit; tests prove isolation before more tables exist. |
| Access control | **Registration is a request, not an account**: user picks an existing workspace (searchable dropdown, no free text), account is `PendingApproval` and cannot sign in until the workspace admin approves via a single-use emailed link (confirm page, not a state-changing GET). Hangfire dashboard limited to `Hangfire:AdminEmails`. | Keeps strangers out without an allow-list; colleagues join the right workspace. Until Feature 20 lands (Sprint 2), Phase 1 registration behaviour continues for dev/test users. |
| Email provider | **SendGrid** (v3 send API, Inbound Parse, Event Webhook) behind `IEmailSender` in Application | Same swappable-interface rule as `IEmailComposer`. |
| Webhook auth | SendGrid **signed event webhook** (ECDSA public key verify) for events; shared secret in the Inbound Parse URL for inbound (Parse has no signing) | Webhooks are unauthenticated by JWT; must be verified before any processing. |
| Secrets | `user-secrets` for all development until F30; Azure Key Vault via managed identity wired in F30 (code reads config the same way, so the swap is a config-source change) | Parent plan §7/§9. Costs cents/month at this scale (§1a); keeps secrets out of app settings and deploy config. |
| Logging | Serilog → Application Insights sink from the first deploy | Parent plan §9. |
| Blob staging | CSV uploaded to blob storage (`imports/{workspaceId}/{batchId}.csv`) behind `IBlobStorage`, parsed by a Hangfire job; **Azurite locally throughout Phase 2**, Azure Blob wired in F30 | Keeps large uploads off request threads. |
| Lead discovery provider | **People Data Labs (PDL)** for now (owner decision 2026-10-08), behind `ILeadSource` (Application) with a provider registry so Apollo.io or another vendor is a one-class swap. Build against `FakeLeadSource` first. | Swappable like `IEmailComposer`. Apollo API needs Professional+ (not Basic); PDL is ~$98/mo for 350 records, pay per match. Verify prices on vendor pages and check each vendor's terms for storing/using results before committing. |
| Template syntax | Simple `{{firstName}}`, `{{company}}`, `{{title}}` token replacement (no Liquid/Razor) | Enough for MVP; avoids template-injection surface. |
| Email send idempotency | `EmailMessage` row inserted (status `Queued`) **before** calling SendGrid; enrollment advanced in the same transaction as the sent-marking | Prevents double-sends on Hangfire retry. |
| Open tracking | SendGrid open pixel via Event Webhook; treat opens as approximate (Apple MPP) | Report honestly in analytics UI. |
| API versioning/proxy | All new routes under `/api/v1/...`; **add each new path prefix to `ZenLead.Client/src/proxy.conf.js`** | Otherwise `ng serve` 404s. |
| Migrations in prod | Applied by a CI/CD step (`dotnet ef migrations bundle`), never on app start in prod | Avoid slot-swap races and surprise schema changes. |

Note: parent plan §6 lists `POST /ai/compose-email` as `/api/v1`; Phase 1 already followed that.

### 1a. Infrastructure cost (single-organisation baseline)

Prices are USD, list price, US regions, checked 2026-10-07. Rows marked *est.* are not from a source I checked — verify in the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) before committing. Re-check at Sprint 1 kickoff and in the PBI 31.3 cost audit.

| Resource | Original plan | Single-org choice | Approx. monthly | Source / note |
|---|---|---|---|---|
| App Service plan (Linux) | B1/S1 + `staging` slot (slots need S1+; S1 ≈ $69 *est.*) | **B1**, no slot | **$13.14** | [Azure App Service pricing](https://azure.microsoft.com/pricing/details/app-service/). Don't use Free/F1: no always-on, CPU quota — breaks the in-process Hangfire sender job. |
| Azure SQL | Basic/S0 (S0 ≈ $15 *est.*) | **Free serverless offer** (100k vCore-s, 32 GB/month), fallback **Basic** | **$0** (fallback $5) | [Free offer](https://learn.microsoft.com/azure/azure-sql/database/free-offer), Basic $5 for 5 DTU/2 GB. Hangfire polling may keep the DB awake and burn the free vCore-seconds; if it does, switch to Basic. Basic's 2 GB cap is fine for ~50k leads but watch it. |
| Key Vault (Standard) | Yes | **Yes**, access via managed identity | **< $0.01** | ~$0.03 per 10k operations *est.*; a handful of reads at startup. Don't use Premium (HSM). |
| Storage account / Blob | Yes | **Yes**, LRS, Hot, private, 30-day lifecycle rule | **$0.05–0.50** *est.* | ~$0.018/GB/month *est.*; CSVs ≤ 10 MB deleted after 30 days. Also protects imports from App Service local-disk loss on restart/redeploy. |
| App Insights / Log Analytics | Yes | Yes, **daily ingestion cap** | $0–3 *est.* | First 5 GB/month ingestion free *(from memory)*; the cap prevents surprise bills. |
| Domain | ~$15/yr | Already owned (`zenraxconsultancy.com`, GoDaddy) | $0 | Subdomains are free; see §1b for DNS layout. |
| **Azure subtotal** | ≈ $100+ *est.* with slot/S1 | | **≈ $15–25** | Budget alert at **$40** leaves headroom. |
| SendGrid | Essentials | Cheapest plan that supports Inbound Parse + Event Webhook | **from $19.95** | The permanent free plan is gone; only a 60-day, 100/day trial remains ([pricing overview](https://automationatlas.io/answers/sendgrid-pricing-explained-2026/)). Amazon SES (~$0.10/1k *est.*) would be cheaper, but `IEmailSender` has no inbound-parse equivalent there — a swap means rebuilding Feature 25. Revisit only if SendGrid becomes the main line item. |
| OpenAI | GPT-4o | GPT-4o for compose; **evaluate GPT-4o-mini** for classification/personalisation | usage-based | GPT-4o $2.50/$10 per 1M in/out tokens; GPT-4o-mini $0.15/$0.60 ([source](https://www.analyticsvidhya.com/blog/2024/12/openai-api-cost/)). Rough: 1,000 calls at ~500 in / 300 out tokens ≈ $4.25 on GPT-4o, ≈ $0.26 on mini. Keep the per-environment spending cap. |
| **Realistic total** | $105–175 | | **≈ $35–50 + OpenAI usage** | Dominated by SendGrid. |

**Budget alerts:** Azure Cost Management at $40/month (Azure only); OpenAI hard cap set in the OpenAI dashboard.

**First upgrades if usage grows** (in order): Azure SQL Basic → S0; B1 → S1 and add the staging slot once there are customers who'd notice a bad deploy; Premium-tier or additional vaults only if compliance requires it.

### 1b. Domain & DNS layout (`zenraxconsultancy.com`, GoDaddy)

| Purpose | Host (GoDaddy "Name" field = the part before `.zenraxconsultancy.com`) | Type | Value |
|---|---|---|---|
| App (App Service custom domain) | `leads` | CNAME | `<app>.azurewebsites.net` |
| App domain ownership | `asuid.leads` | TXT | verification id shown by App Service |
| SendGrid domain authentication (DKIM/return-path) | records SendGrid generates for the **root** domain `zenraxconsultancy.com` (e.g. `s1._domainkey`, `em1234`) — sender is `info@zenraxconsultancy.com` | CNAME | per SendGrid |
| Inbound replies (Inbound Parse) | `reply.leads` | MX (priority 10) | `mx.sendgrid.net` |

Rules: a name with a CNAME (`leads`) cannot also hold MX/TXT, so inbound uses the **`reply.leads`** child name, not `leads` itself. MX on a subdomain does not affect the root domain's mail (Microsoft 365/Google/etc.). App Service managed certificates are free on B1 and above, so HTTPS on `leads.` costs nothing. **Owner decision (2026-10-08): outreach is sent from `info@zenraxconsultancy.com` on the root domain**, so the subdomain reputation isolation originally planned is not in place — cold-campaign complaints/bounces can affect the company's normal mail. Mitigate with low starting volumes, warm-up caps, DMARC monitoring (`p=none` first; do not replace an existing root DMARC/SPF), and keep the option to move to a `leads.` sending subdomain later (see `phase-2-features/17-sendgrid-dns.md`).

### 1c. Local development environment (until Feature 30)

| Need | Local substitute | Note |
|---|---|---|
| Database + Hangfire storage | SQL Server LocalDB (already used) | Same provider as Azure SQL. |
| Blob storage | Azurite | Behind `IBlobStorage`. |
| Secrets | `dotnet user-secrets` | Keys: `Jwt:*`, `OpenAI:ApiKey`, `SendGrid:ApiKey`, webhook secrets, `LeadSource:ApiKey`. |
| Public URL for SendGrid webhooks (Inbound Parse, Event Webhook) from Sprint 3/4 | A **persistent** dev tunnel (e.g. Visual Studio Dev Tunnels, or ngrok with a reserved domain) pointing at `https://localhost:7242` | The URL must be stable or the SendGrid settings need editing every session. The tunnel exposes the local API to the internet: keep webhook secret/signature checks on and only run it while testing. |
| Serilog | Console + file sinks; Application Insights sink added in F30 | Keep the sink behind config. |
| Unsubscribe link base URL | Config value (`App:PublicBaseUrl`), the tunnel URL in dev | Links in real test emails must resolve. |

Consequence: Gate 2 cannot be rehearsed on Azure until Sprint 5, and "merge to `main` auto-deploys" only exists from F30 on.

---

## 2. Sprint 1 — Leads: model, discovery & CSV import (Weeks 4–5)

Goal: the product's core loop exists first — **discover, manage and monitor leads**. Multi-tenant foundation (workspace query filter + isolation tests); lead model with companies and provenance; leads list/detail; target profiles and automated discovery on the Hangfire job runner; local dev environment ready (§1c); SendGrid DNS started for Sprint 4. Complete lead management lands in this sprint, including CSV import. Order: F11 → F12 → F13 → F14 → F15 → F16 (F17 in parallel). Heaviest sprint of the phase: F14 and F15 are the must-haves; if time runs short F16 (wizard polish) and F13.2 bulk-select slip into Sprint 2 first.

### Feature 11 — Company Entity, Lead Model & Ingestion Service (branch: `feature/company-lead-extension`)
- **PBI 11.1** `Company` (WorkspaceId, Name, Domain, Industry, Country); `Lead.CompanyId` nullable FK; unique index `(WorkspaceId, Email)` on `Lead` (normalised lowercase) — **check for existing duplicates in the migration** and handle them (merge or fail loudly).
- **PBI 11.2** Lead CRUD completion: `PUT /leads/{id}`, `DELETE /leads/{id}` (soft delete preferred so campaign history survives), `Status` transitions rules in Domain.
- **PBI 11.3** `ComposeEmailUseCase` now pulls `Company` data instead of free-text-only context (keep `context?` as an override).
- **PBI 11.4 — Shared `LeadIngestionService`** (Application; pure logic unit-tested, persistence batched): takes candidate leads (name, email, title, company name/domain/industry/country, `Source`, `SourceRunId`) and per row does email syntax validation + lowercase, in-batch duplicate detection, DB duplicate detection (batched `WHERE Email IN (...)`), skip of leads already `Unsubscribed`/`Bounced`, company upsert by domain/name, insert. Returns per-row `Imported | Duplicate | Suppressed | Invalid(reason)`. **Single code path for every way a lead enters the system** — discovery (F14) and CSV (F15) both call it; neither re-implements dedupe.
- **PBI 11.5 — Lead provenance columns**: `Lead.Source` (`Manual|Csv|Discovery`), `Lead.SourceRunId` nullable, `Lead.EmailVerificationStatus` (`Unverified|Verified|Risky|Invalid`, default `Unverified`). One migration, here.
- **PBI 11.6 — Tests**: ingestion service (in-batch dupes, existing-DB dupes, case-differing emails, suppressed statuses, invalid emails, company upsert, dedup per workspace).
- **PBI 11.7 — Tenant foundation**: `ICurrentWorkspace` (Application interface; workspace id from the JWT `workspace_id` claim in HTTP, passed explicitly into background jobs and webhook handlers — jobs take `workspaceId` as an argument, never ambient state). New tenant-owned entities implement a marker `ITenantEntity { WorkspaceId }` and are stamped on insert in `SaveChanges`. Until Feature 20, registration stays as built in Phase 1 (dev/test users only).
- **PBI 11.8 — EF Core global query filter** on `ITenantEntity` reading `ICurrentWorkspace` (this is the parent plan's tenant-isolation hardening task); explicit `IgnoreQueryFilters()` only in code paths that run without an HTTP context and filter by the explicit `workspaceId` instead. Keep the Phase 1 manual `workspace_id` checks in `LeadsController`/`ComposeEmailUseCase` (defence in depth).
- **PBI 11.9 — Tenant-isolation tests**: two workspaces; user A cannot list, read, update or delete user B's leads/companies/target profiles; the Phase 1 cross-tenant regression test still passes; unique `(WorkspaceId, Email)` allows the same email in different workspaces. **Every new tenant-owned endpoint added later gets one cross-workspace test case.**
### Feature 12 — App Shell (branch: `feature/angular-shell`)
- **PBI 12.1** Material sidenav + toolbar with nav to Leads/Campaigns/Inbox/Analytics (stubs for later sprints), workspace name, logout. No role-aware UI.

### Feature 13 — Leads UI: List & Detail (branch: `feature/angular-leads-list`)
- **PBI 13.1** Leads list upgrade: server-side paging, sort, search (name/email/company), filter by status/company/source/`SourceRunId`; `GET /leads` takes `page,pageSize,q,status,companyId,source,sourceRunId,sort`; indexes to support it.
- **PBI 13.2** Lead detail: show company, source (manual/CSV/discovery + link to the run), email verification status, status history placeholder, edit/delete; bulk-select (for target profiles now, enrollment in Sprint 3).

### Feature 14 — Automated Lead Discovery (branch: `feature/lead-discovery`)
Finds new leads that match a **Target Profile** — a saved description of who we want to reach (not a person). A profile is either written from scratch or built from a lead we already have. **Provider: People Data Labs for now** — everything provider-specific lives behind `ILeadSource` (selected by `LeadSource:Provider`), so switching to Apollo.io or another vendor only means adding one Infrastructure class. Every discovered lead goes through the shared `LeadIngestionService` (F11.4); it never bypasses dedupe.

Terms: a **lead** is always a person we might email (created by hand, CSV, or discovery — see `Lead.Source`). A **Target Profile** is search criteria. A **discovery run** executes a profile against the provider and produces leads.
- **PBI 14.0 — Hangfire setup** (first background-job feature): SQL Server storage (LocalDB locally), dashboard at `/hangfire` restricted to `Hangfire:AdminEmails`, retry policy (3 attempts, exponential), dead-letter visibility. Reused by every later job (CSV, sender, classification).
- **PBI 14.1 — Abstraction**: `ILeadSource` in Application (`SearchAsync(LeadSearchCriteria, cursor, limit) → page of DiscoveredLead`, plus `GetRemainingCreditsAsync`). `LeadSearchCriteria`: job titles, industries, countries, company size range, company domains (all optional lists). Dev/CI `FakeLeadSource` with canned results; **no real provider calls in tests**. Provider impl (`ApolloLeadSource` or `PdlLeadSource`) added once the vendor is decided; API key in user-secrets (`LeadSource:ApiKey`), Key Vault in F30.
- **PBI 14.2 — Target Profile**: entity `TargetProfile` (WorkspaceId, Name, CriteriaJson, optional `SourceLeadId`, CreatedBy). CRUD: `GET/POST /api/v1/target-profiles`, `PUT/DELETE /api/v1/target-profiles/{id}`.
- **PBI 14.3 — Build a profile from leads**: `POST /api/v1/target-profiles/from-leads` takes one lead id (that lead's title, company industry, company size and country become the criteria) or several (merges the most frequent titles, industries, countries; optionally only `Replied`/interested leads) and returns a **suggested, unsaved** profile the user edits and saves. Pure Domain/Application logic, unit-tested.
- **PBI 14.4 — Persistence**: `LeadDiscoveryRun` (WorkspaceId, TargetProfileId, CriteriaJson snapshot, Provider, RequestedCount, FoundCount, ImportedCount, SkippedDuplicateCount, SkippedSuppressedCount, CreditsUsed, Status `Queued|Running|Completed|Failed|CapReached`, CreatedBy). `Lead.Source`/`SourceRunId` (added in F11.5) trace every discovered lead to its run and profile.
- **PBI 14.5 — Run & import job**: `POST /api/v1/target-profiles/{id}/runs` (max leads, hard-capped per run) and `GET /api/v1/discovery/runs/{id}` → Hangfire `ProcessLeadDiscoveryJob(runId, workspaceId)`: pages through the provider, drops results without a usable email, hands results to `LeadIngestionService` (dedupe against existing leads and leads already `Unsubscribed`/`Bounced`; the full suppression list arrives with F23.4 and is added to the ingestion check then), inserts leads with status `New` and the provider's verification flag — idempotent and resumable (batched commits, safe to re-run). Provider 429/5xx → backoff then `Failed` with reason. Runs start only on user action (no automatic runs).
- **PBI 14.6 — Cost guard**: per-workspace monthly discovery-credit cap (config `LeadSource:MonthlyCreditCap`, same pattern as the AI cap); checked before each provider page; run ends `CapReached` rather than overspending. `GET /api/v1/discovery/credits` for the UI meter.
- **PBI 14.7 — Email quality gate**: discovered leads store `EmailVerificationStatus` (column from F11.5) from the provider's own flag where available; **campaign enrollment skips `Invalid`, and warns on `Unverified`/`Risky`**. A separate verifier (`IEmailVerifier`) is a later add-on, decided with the provider.
- **PBI 14.8 — UI**: "Target profiles" screen (list, create/edit with criteria chips) in the Leads area; **"Create target profile from this lead"** on lead detail and from list bulk-select; "Run discovery" with max-leads input, credit meter, progress (poll), result summary (found / imported / duplicates / suppressed) and a link to the imported leads filtered by `SourceRunId`. No proxy change needed (all under `/api`).
- **PBI 14.9 — Tests (priority)**: profile-from-leads (single and merged); dedupe against existing and suppressed emails; no-email results dropped; job re-run idempotence; cap stops the run mid-way; provider failure/retry; `FakeLeadSource` used throughout.

### Feature 15 — CSV Import Backend (branch: `feature/csv-import-backend`)
Highest-risk file-handling feature; test-first. Leads enter through the shared ingestion service (F11.4), already proven by discovery in Sprint 1.
- **PBI 15.1 — Domain/persistence**: `CsvImportBatch` (FileName, BlobPath, ColumnMapping json, RowCount, ImportedCount, SkippedDuplicateCount, ErrorCount, ErrorLog, Status `Uploaded|Parsing|Completed|Failed`, CreatedBy).
- **PBI 15.2 — Upload & preview**: `POST /api/v1/leads/import` (multipart; size limit e.g. 10 MB / 50k rows, `.csv` only) → store via `IBlobStorage` in Application (Azurite impl until F30; Azure Blob impl using managed identity added in F30) → return `batchId` + header row + first N sample rows for the mapping UI.
- **PBI 15.3 — Mapping & start**: `POST /api/v1/leads/import/{batchId}/start` with column mapping (name, email, title, company name/domain/industry/country); validate mapping covers required `email`.
- **PBI 15.4 — Parser** (`ZenLead.Domain`/`Application`, pure & unit-tested): streaming CSV read (CsvHelper), BOM/encoding/delimiter handling, trim, header→field mapping; rows are handed to the shared **`LeadIngestionService` (F11.4)** for validation, dedupe and company upsert (not re-implemented here). Per-row result: `Imported | Duplicate | Suppressed | Invalid(reason)`.
- **PBI 15.5 — Hangfire job** `ProcessCsvImportJob(batchId, workspaceId)`: idempotent (resumable/rerunnable without duplicating leads), batches of ~500 rows per transaction, updates batch counters, writes error log (capped, with row numbers), sets final status. Takes `workspaceId` explicitly (no HTTP context).
- **PBI 15.6 — Status & errors**: `GET /api/v1/leads/import/{batchId}/status` (progress, counts), `GET .../errors` (downloadable CSV of rejected rows).
- **PBI 15.7 — Hangfire** is already set up by F14.0; this job just registers on it.
- **PBI 15.8 — Tests (priority)**: malformed quotes, empty file, header-only, missing email column, unicode names, 10k-row perf sanity, job re-run idempotence, file-level concerns only (dedupe cases themselves are covered by F11.6).

### Feature 16 — Leads UI: Import Wizard (branch: `feature/angular-import-wizard`)
- **PBI 16.1** Import wizard (Material stepper): upload → map columns (auto-guess by header name) → review → progress (poll status every 2s) → result summary with error-CSV download.
- **PBI 16.2** Targeted Angular tests (wizard mapping auto-guess logic).

### Feature 17 — SendGrid Domain & Inbound DNS Kickoff (branch: `chore/sendgrid-dns` — mostly non-code)
- **PBI 17.1** SendGrid account, API key (user-secrets until F30), domain authentication (DKIM/return-path CNAMEs) for `zenraxconsultancy.com` (records added at GoDaddy; see §1b).
- **PBI 17.2** Create inbound subdomain (`reply.leads.zenraxconsultancy.com`), **MX record → `mx.sendgrid.net`**, configure Inbound Parse destination URL placeholder. Record DNS TTL/propagation status in `docs/`. *Do this in week 4; Sprint 4 depends on it.*
- **PBI 17.3** Decide and document sender identity model (one verified sender for Zenrax; per-workspace senders deferred) — needed for `Campaign.FromSenderId`.

**Sprint 1 demo / exit:** (local) a user in a second (test) workspace cannot see or fetch the first workspace's leads (isolation tests green); leads list is pageable/searchable/filterable with source shown; create a target profile (from scratch and from an existing lead), run discovery, and see new, deduped, source-tagged leads (fake source until a provider is chosen; one real run against the chosen provider's free/test tier before Gate 2); upload a 5–10k row messy CSV locally with duplicates/invalids reported per row, re-uploading the same file imports zero new rows, and imported leads appear in the same list as discovered ones; SendGrid DNS records created and propagating.

---

## 3. Sprint 2 — Email foundation, accounts & campaign domain (Weeks 6–7)

Goal: sending groundwork, account management (colleagues joining the shared workspace through admin approval, a super admin who creates workspaces, forgot/reset password) and the campaign model/API, so Sprint 3 can concentrate on the sending engine and campaign UI. Order: F19 (super admin; no email dependency, starts on day one) ∥ F18 (email sender) → F20 (approval emails need F18, and the end-to-end demo needs a workspace created through F19; F20 tests create workspaces through fixtures) → F21 (reset emails need F18); F22 (campaign domain; can use the fake `IEmailSender`) in parallel. **This is a heavy sprint (five features):** if it overruns, F22 (campaign domain) slips to the start of Sprint 3 and F24 (campaigns UI) is the first thing cut from Sprint 3.

### Feature 18 — Transactional Email Sender (branch: `feature/email-sender`)
*(Workspace members/roles/invites and the members UI — the former members/roles features — are deferred, see §14.)*
- **PBI 18.1 — `IEmailSender` + SendGrid client** with a dev "log to console" fake. (Campaign sending in Feature 23 and registration-approval emails in Feature 20 reuse this interface.)
- **PBI 18.2 — Workspace fields**: extend `Workspace` with `TimeZone` (needed for send windows); `PlanTier`/`Country` deferred.
- **PBI 18.3 — Tests**: fake sender, SendGrid payload mapping.

### Feature 19 — Super Admin & Workspace Creation (branch: `feature/super-admin-workspaces`)
A platform-level **super admin** whose only capability is creating (and listing) workspaces. It is not a member of any workspace and cannot see or touch tenant data. Independent of F20; can be built in parallel.
- **PBI 19.1 — Platform role & seeding**: `AppUser.PlatformRole` (`None | SuperAdmin`); `AppUser.WorkspaceId` becomes nullable (super admin has none). A startup seeder creates the super admin **idempotently** from config `SuperAdmin:Email` and `SuperAdmin:Password` (user-secrets locally, Key Vault in F30): created if absent, never overwrites an existing password, never logs the password. **No default or hard-coded credentials in code, migrations, `appsettings*.json` or this repo**; the credentials are chosen by the project owner and shared with no one else. In production the app fails fast if they are missing and the account has not been seeded yet.
- **PBI 19.2 — Tokens & enforcement**: the super admin JWT carries `role=SuperAdmin` and **no `workspace_id` claim**. Authorization policy `SuperAdminOnly` guards `/api/v1/admin/**`; every tenant endpoint requires a `workspace_id` claim, so the super admin gets 403 on leads/campaigns/inbox/etc., and the F11 query filter returns nothing for a null workspace. Regular users get 403 on `/admin/**`. Endpoint-matrix integration test covers both directions.
- **PBI 19.3 — Workspace API**: `POST /api/v1/admin/workspaces {name, adminEmail}` (unique name, validated email; sets `Workspace.AdminEmail`, default `TimeZone`), `GET /api/v1/admin/workspaces` (id, name, adminEmail, user count, created date — no tenant content). Optional: email the new workspace admin a "your workspace is ready, register here" note via `IEmailSender`. **Nothing creates a workspace except the super admin** — no seeder, migration or config creates "Zenrax"; the super admin creates it through this API/UI (name "Zenrax" + the admin email), which is therefore also the first step of the Gate 2 / UAT script. Workspace rename/delete and admin change are out of scope.
- **PBI 19.4 — Angular**: after login the super admin lands on `/admin/workspaces` (route guard on role): list + "Create workspace" dialog. Tenant navigation (leads/campaigns/inbox/analytics) is not rendered for this role; the tenant guard redirects the super admin to the admin area. No proxy change needed.
- **PBI 19.5 — Hardening of the account**: login rate limit and lockout apply (same as everyone); strong password policy enforced at seeding (reject weak `SuperAdmin:Password`); audit log entry (who/when) for every workspace creation; no MFA in Phase 2 (called out in §13 risks).
- **PBI 19.6 — Tests (priority)**: seeder idempotent and never resets the password; seeder skipped/failed correctly when config is missing; super admin gets 403 on every tenant endpoint and a null-workspace query returns nothing; regular and workspace-admin users get 403 on `/admin/**`; create-workspace validation (duplicate name, bad admin email); the new workspace appears in the F20 registration picker.

### Feature 20 — Workspace Membership & Registration Approval (branch: `feature/workspace-membership`)
Colleagues share one workspace. Registering does **not** create an account you can use: the user picks an existing workspace, and that workspace's admin approves them by clicking a link in an email. One admin per workspace (for now an admin email); no roles beyond admin/member. Needs F18 (`IEmailSender`); tests use the fake sender, and in dev the "log to console" sender prints the approval link.
- **PBI 20.1 — Data model**: `Workspace.AdminEmail` (set when the super admin creates the workspace, F19.3 — workspaces are never auto-created or seeded); `AppUser.Status` (`PendingApproval | Active | Rejected | Disabled`); `RegistrationRequest`/approval token table (UserId, WorkspaceId, TokenHash, ExpiresAt, UsedAt) — raw token never stored. Migration: existing Phase 1 users are set `Active` in their current workspace so dev databases keep working; no workspace is created or renamed by the migration (dev databases are simply reset when convenient; no production data exists).
- **PBI 20.2 — Workspace picker API**: public `GET /api/v1/workspaces?q=` (rate-limited, returns only `{id, name}`, max ~20 results, prefix/contains search) feeding a searchable dropdown. No free-text workspace on registration: `POST /auth/register` takes `workspaceId` and rejects unknown ids. Exposes workspace names to anonymous callers — acceptable while the only tenant is Zenrax; revisit before onboarding other organisations.
- **PBI 20.3 — Registration**: `POST /api/v1/auth/register {email, password, displayName, workspaceId}` creates the user as `PendingApproval` in that workspace (no tokens issued), generates a single-use approval token (random 256-bit, hashed, expires in 7 days), and emails the workspace admin (`Workspace.AdminEmail`) the requester's name/email plus the approval link. Response is the same whether or not the email is already registered (no account enumeration); a duplicate pending request re-sends at most once per hour. Cap on pending requests per workspace (e.g. 20) and per-IP/per-email rate limits so strangers can't flood the admin's inbox.
- **PBI 20.4 — Approval link**: link target is an Angular page `/approve?token=…` (not a state-changing GET, so mail scanners/link previewers can't approve by prefetching). The page shows who is requesting and offers **Approve** / **Reject**, which call `POST /api/v1/auth/approvals/{token}/approve|reject`. The token is the credential (possession of the admin's mailbox); it is single-use, expires, and approving sets `Active` and emails the user "you're approved, sign in". Reject sets `Rejected` (generic "not approved" at login). Approving an already-used or expired token returns a clear error; the admin can request a fresh link via a "resend" for pending users (stretch).
- **PBI 20.5 — Login & tokens**: `Pending`/`Rejected`/`Disabled` users cannot log in or refresh (generic message for pending: "awaiting approval"); existing refresh tokens are revoked when status leaves `Active`. JWT `workspace_id` claim unchanged, so the F11 query filter keeps working.
- **PBI 20.6 — Angular**: register screen with a searchable workspace dropdown (type-ahead against PBI 20.2, required selection), "request sent, waiting for approval" screen, approval page (19.4), login message for pending users. No proxy change needed (everything is under `/api`).
- **PBI 20.7 — Tests (priority)**: register into an unknown workspace id rejected; pending user cannot log in or use a previously issued refresh token; approval token single-use, expired, wrong-token and tampered cases; approving activates and the user then sees the workspace's leads (second user shares the same data); same response for duplicate registration; pending-request cap and re-send throttle; admin email receives the link (fake sender); isolation tests from F11 still green with two workspaces.
- **Deliberately not in this feature**: user-management UI for the admin (list/disable users), changing the admin, multiple admins/roles, invite-by-admin flow (password reset is Feature 21, super admin is Feature 19).

### Feature 21 — Forgot & Reset Password (branch: `feature/password-reset`)
For **all users, including the super admin**. Needs F18 (`IEmailSender`); tests use the fake sender.
- **PBI 21.1 — Data**: `PasswordResetToken` (UserId, TokenHash, ExpiresAt, UsedAt, RequestedIp). Own table with hashed random 256-bit tokens (not ASP.NET Identity's data-protection tokens, which would depend on a persistent Data Protection key ring across restarts/instances in Azure).
- **PBI 21.2 — Forgot password**: `POST /api/v1/auth/forgot-password {email}` always returns the same 200 response and takes similar time whether or not the account exists. If the account exists and is `Active` (or the super admin), a reset email with a link to the Angular page `/reset-password?token=…` is sent; token expires in 1 hour; earlier unused tokens for that user are invalidated; throttled per email and per IP (e.g. 3 per hour per email). `PendingApproval`/`Rejected`/`Disabled` users get no email.
- **PBI 21.3 — Reset password**: `POST /api/v1/auth/reset-password {token, newPassword}` — validates token (exists, unexpired, unused, constant-time hash compare) and the password policy, sets the new password, marks the token used, **revokes all refresh tokens** for the user (all sessions signed out), and sends a "your password was changed" notice email. Invalid/expired/used tokens return one generic error.
- **PBI 21.4 — Change password (signed in)**: `POST /api/v1/auth/change-password {currentPassword, newPassword}` requires the current password, applies the same policy, revokes other refresh tokens, sends the same notice email.
- **PBI 21.5 — Angular**: "Forgot password?" link on login → request form (neutral confirmation text), reset page reading the token from the URL (clear the token from the address bar/history after load), change-password form in the account menu; works for the super admin role too. The reset link base URL comes from `App:PublicBaseUrl` (dev tunnel locally).
- **PBI 21.6 — Tests (priority)**: same response/timing-shape for existing vs unknown email; token single-use, expired, tampered, replay after success; old password stops working and all refresh tokens die after reset; throttle limits; pending/rejected users get no email; super admin can reset; weak new password rejected; change-password wrong current password rejected.

### Feature 22 — Campaign Domain & Builder API (branch: `feature/campaigns-backend`)
- **PBI 22.1** Entities: `Campaign` (Name, Status `Draft|Active|Paused|Completed`, FromSenderId, daily send cap, send window/timezone), `CampaignStep` (Order, DelayDays, SubjectTemplate, BodyTemplate, UseAiPersonalisation), `CampaignEnrollment` (CampaignId, LeadId, CurrentStep, NextSendAt, Status `Active|Paused|Completed|Replied|Unsubscribed|Bounced|Failed`), unique `(CampaignId, LeadId)`.
- **PBI 22.2** Scheduling logic in **Domain** (pure, heavily tested): `NextSendAt` computation from delay days respecting workspace time zone + send window; step advancement; completion after last step; stop conditions (reply, unsubscribe, bounce).
- **PBI 22.3** Endpoints: `GET/POST /campaigns`, `PUT /campaigns/{id}`, `POST /campaigns/{id}/steps` (+ reorder/delete), `POST /campaigns/{id}/enroll` (lead ids or filter; skips Unsubscribed/already enrolled/invalid), `POST /campaigns/{id}/activate|pause`.
- **PBI 22.4** Activation validation: ≥1 step, sender verified, template tokens resolvable, steps ordered; steps immutable-ish once active (edits affect only not-yet-sent enrollments — document the rule).
- **PBI 22.5** Template rendering (`{{token}}` replace, missing-token fallback/blocking rule) + **unsubscribe link** token injected into every email (required for deliverability/compliance).

**Sprint 2 demo / exit:** a test email goes out through `IEmailSender`; via API a campaign with steps can be created, leads enrolled (skipping unsubscribed/already enrolled), template tokens render, scheduling logic passes its test suite; nothing sends campaign mail yet. A colleague registers by picking the Zenrax workspace, cannot sign in yet, the admin approves via the emailed link, and the colleague then sees the same leads. The super admin signs in, sees only the workspace-admin area (403 everywhere else), and creates the "Zenrax" workspace (with its admin email) which then appears in the registration dropdown; any user can use forgot-password and sign in with the new password (old sessions are signed out).

---

## 4. Sprint 3 — Sending engine & campaigns UI (Weeks 8–9)

### Feature 23 — Sending Engine (branch: `feature/sendgrid-sending-engine`)
- **PBI 23.1** `EmailMessage` entity (WorkspaceId, EnrollmentId, StepId, SendGridMessageId, Subject, Body, Status, QueuedAt, SentAt, OpenedAt, BouncedAt, Error) and `IEmailSender.SendCampaignEmailAsync` (custom args carry `emailMessageId`/`workspaceId` for webhook correlation; `Message-ID`/`In-Reply-To` headers for threading; **Reply-To = inbound subdomain address encoding the thread/enrollment id** — ties into Sprint 4).
- **PBI 23.2** Hangfire recurring job (every 1–5 min): query due active enrollments (`NextSendAt <= now`), process in batches, per-workspace daily cap + per-minute throttle, idempotent send (see §1), optional AI personalisation call per step (log to `AiGenerationLog`), advance enrollment, mark Completed after last step. Failures: retry with backoff, then mark enrollment `Failed` with reason; never block other enrollments.
- **PBI 23.3** `AiGenerationLog` table + per-workspace monthly soft cap (parent §9): every OpenAI call (compose, personalisation, later classification) writes tokens/cost; cap check before call; friendly error + UI banner when exceeded. Migrate Phase 1 `ILogger`-only usage into this.
- **PBI 23.4** Unsubscribe endpoint (public, token-signed) → sets lead `Unsubscribed`, pauses enrollments; global per-workspace suppression check before every send.
- **PBI 23.5** Tests: scheduling across time zones/DST, send-window edge cases, idempotency under job retry (simulate crash between SendGrid call and DB commit → documented behavior), cap enforcement, suppression, fake `IEmailSender`.

### Feature 24 — Campaigns UI (branch: `feature/angular-campaigns`)
- **PBI 24.1** Campaign list (status chips, enrolled/sent counts), create/edit.
- **PBI 24.2** Step builder: add/reorder/delete steps, delay days, subject/body editor with token insert helper, **"Preview with lead"** and **"Generate with AI"** (reuses compose endpoint), display of resulting send timeline (e.g. "Day 0, Day 3, Day 7") — unit test the schedule-display logic.
- **PBI 24.3** Enrollment: select leads (from leads list bulk-select or "all matching filter"), preview skipped counts, confirm; enrollment view table (lead, current step, next send, status).
- **PBI 24.4** Activate/pause controls with validation errors surfaced; sender-domain status indicator (verified/not) linking to setup help.

**Sprint 3 demo / exit:** a 3-step campaign activated locally sends step 1 to enrolled test leads via SendGrid (real inbox received), follow-ups fire on schedule (shorten delays to minutes in a test campaign), unsubscribe link works, AI cost rows visible.

---

## 5. Sprint 4 — Unified inbox (Weeks 10–11)

### Feature 25 — Inbound Parse Webhook & Threading (branch: `feature/inbound-webhook`)
- **PBI 25.1** Entities: `InboxThread` (WorkspaceId, LeadId, CampaignId?, Subject, LastMessageAt, Status/IsRead), `InboxMessage` (ThreadId, Direction `Inbound|Outbound`, From, To, Subject, Body (text + sanitized html), ReceivedAt/SentAt, Classification, ClassificationConfidence, RawMessageId). Outbound campaign sends create/attach to a thread.
- **PBI 25.2** `POST /api/v1/webhooks/sendgrid/inbound` (anonymous, secret-in-URL verified, size-limited): parse multipart, resolve workspace/thread via Reply-To encoding → fallback to `In-Reply-To`/References header → fallback to sender email match; unmatched → quarantine log, never dropped silently. Strip quoted reply text/signatures best-effort; **sanitize HTML** (store text primarily; escape on render).
- **PBI 25.3** Idempotency on `Message-ID`; reject auto-replies/OOO/bounce-DSNs from being classified as human replies (header heuristics: `Auto-Submitted`, `X-Autoreply`, `Precedence`).
- **PBI 25.4** On inbound: lead → `Replied`, enrollment → `Replied` (sequence stops) *unless* classification says otherwise (OOO).
- **PBI 25.5** Tests with captured SendGrid payload fixtures (multipart), threading fallbacks, duplicate delivery, spoofed/unknown sender, unknown-sender routing.

### Feature 26 — AI Reply Classification (branch: `feature/reply-classification`)
- **PBI 26.1** `IReplyClassifier` (Application) → Semantic Kernel impl; structured JSON `{ classification: interested|not_interested|unsubscribe, confidence }`. **Deserialize with `JsonSerializerDefaults.Web`, validate required fields/enum values explicitly, camelCase fixtures in tests** (parent plan risk §11, Phase 1 Feature 6 finding). Unknown/invalid → `Unclassified` + log, never throw into the webhook path.
- **PBI 26.2** Classification runs in a Hangfire job (not inline in the webhook, so SendGrid gets a fast 200); logged to `AiGenerationLog` and subject to workspace cap (over cap → `Unclassified`, still shown in inbox).
- **PBI 26.3** `unsubscribe` classification auto-pauses enrollment, sets lead `Unsubscribed`, adds to suppression (parent §4 Sprint 4). Low-confidence → no auto-action, flagged for manual review.
- **PBI 26.4** Prompt-injection guard: reply text delivered in a delimited block, classifier instructed to ignore instructions inside it; output constrained to the enum. Test with adversarial reply fixtures.
- **PBI 26.5** Tests with fake `IReplyClassifier`; prompt-builder tests.

### Feature 27 — Inbox API & UI (branch: `feature/inbox-ui`)
- **PBI 27.1** `GET /inbox/threads` (paging, filter by classification/unread/campaign), `GET /inbox/threads/{id}` (marks read), `POST /inbox/threads/{id}/reply` (sends via `IEmailSender` with proper threading headers, records outbound `InboxMessage`), manual classification override.
- **PBI 27.2** Angular: thread list (classification chips, unread state, lead/campaign), conversation view, reply composer (optional "AI suggest reply" via compose endpoint — stretch), manual reclassify. Polling every ~30s for new messages (SignalR is out of scope).
- **PBI 27.3** Add `/api/v1/inbox` + `/api/v1/webhooks` to `proxy.conf.js`.

**Sprint 4 demo / exit:** reply to a campaign email from a real mailbox → appears in the inbox within ~1 minute, threaded to the right lead, classified; "unsubscribe" reply auto-pauses the sequence; a user can answer from the inbox.

---

## 6. Sprint 5 — Analytics, hardening, Azure deploy, UAT (Weeks 12–13)

### Feature 28 — SendGrid Event Webhook & Analytics (branch: `feature/analytics`)
- **PBI 28.1** `POST /api/v1/webhooks/sendgrid/events` — **verify ECDSA signature + timestamp** before processing, 401 otherwise; correlate via custom args → update `EmailMessage` (`delivered`, `open`, `bounce`/`dropped`, `spamreport`); idempotent on `sg_event_id`; bounce → enrollment `Bounced` + lead flagged; spam report → suppress.
- **PBI 28.2** `GET /analytics/campaigns/{id}/summary`, `GET /analytics/workspace/summary`: sent, delivered, opened (unique), replied, bounced + rates; date-range filter; computed with indexed aggregate queries (add indexes on `EmailMessage(WorkspaceId, CampaignId, SentAt)`).
- **PBI 28.3** Angular `features/analytics`: workspace dashboard + per-campaign view (stat tiles, per-step breakdown, trend line); caveat tooltip on open rate (MPP). Follow the project's dataviz conventions for charts.
- **PBI 28.4** Tests: signature verification (valid/invalid/replayed), event idempotency/out-of-order, rate math (zero-division, unique opens).

### Feature 29 — Observability, Security & Reliability Hardening (branch: `feature/mvp-hardening`)
- **PBI 29.1** Serilog end-to-end (console/file locally; Application Insights sink in F30) with user enrichers (no PII/email bodies in logs); App Insights alerts move to F30.7 since they need Azure.
- **PBI 29.2** Security pass: rate-limiting on auth + public endpoints (built-in rate limiter), account lockout, password policy, security headers/CSP for SPA, upload validation, webhook auth review, dependency audit (`dotnet list package --vulnerable`, `npm audit`), confirm no secrets in repo/history.
- **PBI 29.3** Authorization sweep: every endpoint added since Sprint 1 requires auth (except unsubscribe/webhooks, which have their own token/signature checks) — covered by an integration test. Each endpoint also has a cross-workspace test (see F11.9).
- **PBI 29.4** Data safety: migration rollback notes (Azure backup/PITR test restore and Blob lifecycle verification moved to F30.7).
- **PBI 29.5** Performance sanity: leads list at 50k rows, sender job at 1k due enrollments, inbox thread list; fix N+1s/indexes found.
- **PBI 29.6** Error/empty/loading states audit across all Angular screens; expired-session behavior; accessibility basics.
- **PBI 29.7** Global (all-workspace) monthly cap for AI and discovery credits alongside the per-workspace caps; per-IP/per-email rate limits on `register`/approval endpoints reviewed. Test: global cap stops calls across workspaces.

### Feature 30 — Azure Provisioning & CI/CD (branch: `feature/azure-cicd`) — *deliberately last; all earlier work runs locally*
First deployment of the app. Everything before it runs on the developer machine (LocalDB, Azurite, user-secrets, tunnel for webhooks — see §1c), so this feature must also prove the production-only code paths (Key Vault, managed identity, Azure Blob, Azure SQL + Hangfire, App Insights) for the first time. Budget a buffer for surprises.
- **PBI 30.1 — Infra provisioning** (Bicep or `az` scripts committed under `infra/` so it's reproducible): resource group, App Service plan (**B1 Linux**, no staging slot) + app, Azure SQL (Basic, or the serverless free offer if Hangfire polling doesn't defeat auto-pause), Application Insights/Log Analytics with a daily ingestion cap. Storage account (Blob, LRS/Hot/private, lifecycle rule deleting `imports/` blobs after 30 days) and Key Vault (Standard). App Service **managed identity** gets Key Vault `get/list` and `Storage Blob Data Contributor` on the storage account (no keys or connection strings in settings).
- **PBI 30.2 — Config & secrets**: move `Jwt:*`, `ConnectionStrings:Default`, `OpenAI:ApiKey` into Key Vault (`AddAzureKeyVault` with `DefaultAzureCredential`); App Service settings hold only the vault URI. Startup fails fast if a required secret is missing.
- **PBI 30.3 — GitHub Actions pipeline**: on PR → build + `dotnet test` + `ng test`; on merge to `main` → `dotnet publish` (triggers `ng build` into `wwwroot`) → run EF migrations bundle against Azure SQL → deploy to the App Service → smoke check (`/health`); a failed smoke check fails the pipeline (no slot swap at this scale). Branch protection requiring green CI.
- **PBI 30.4 — Health & observability baseline**: `/health` endpoint (DB reachable), Serilog + Application Insights sink, request correlation id, CORS/HTTPS/HSTS settings reviewed.
- **PBI 30.5 — Cost tracking**: Azure Cost Management budget alert (Azure alert at $40/month; see §1a) and OpenAI per-env cap documented.
- **PBI 30.6 — First production deploy & parity check**: deploy, apply migrations from scratch against Azure SQL (also run them once against a copy of the dev DB), confirm Hangfire runs on Azure SQL and the sender job survives an app restart, upload a CSV through Azure Blob, send a real campaign email, receive a reply through the production Inbound Parse and Event webhook URLs (switch both from the dev tunnel to `leads.zenraxconsultancy.com`), custom domain + managed certificate (`leads` CNAME and `asuid.leads` TXT, see §1b).
- **PBI 30.7 — Azure-dependent hardening moved from F29**: Application Insights alerts (failed Hangfire jobs, 5xx rate, webhook failures, daily send failures); Azure SQL backup/PITR verified with a test restore; Blob lifecycle rule (delete `imports/` after 30 days) verified.

### Feature 31 — UAT & Gate 2 Readiness (branch: `chore/uat-gate2`)
- **UAT safety rule (owner requirement):** UAT never emails real prospects. Production runs `Sending:Mode=Restricted` with a recipient allow-list of tester-controlled mailboxes (fail-closed: empty list sends nothing); `Mode=Live` is a go-live action after the Gate 2 decision (see F23 and F31 docs).
- **PBI 31.1** UAT script = Gate 2 loop: super admin creates the Zenrax workspace (admin email set) → register (pick the Zenrax workspace; the admin approves by email; password reset checked once) → discover leads from a target profile and/or import a CSV → build campaign (with AI step) → activate → receive email → reply → see classified thread → read analytics. ≥2 Zenrax users (owner + one colleague) each run it on **production** Azure with their own mailbox in the shared Zenrax workspace; log bugs in a UAT sheet (`docs/uat-results.md`), triage P0/P1 fixed in-sprint, rest to backlog.
- **PBI 31.2** Smoke path written as a one-page checklist re-run before each release (parent plan §9).
- **PBI 31.3** Cost audit: Azure + SendGrid + OpenAI actuals vs. the re-baselined target (see §1a: ≈ $35–50/month incl. SendGrid, plus OpenAI usage); project to steady state.
- **PBI 31.4** Gate 2 rehearsal on a clean workspace, then live demo; record decision and Phase 3 backlog.

---

## 7. Gate 2 exit criteria (from parent plan §4 — checklist)

- [ ] MVP deployed to Azure and reachable outside the local network (custom domain, HTTPS)
- [ ] ≥2 Zenrax users (sharing the Zenrax workspace) completed: discover/import leads → build campaign → send → see reply in inbox → read analytics
- [ ] Infrastructure spend within the re-baselined single-org budget (§1a: ≈ $35–50/month incl. SendGrid, plus OpenAI usage; the original $105–175 band no longer applies)
- [ ] (Internal) Cross-workspace access denied (isolation tests green); all non-public endpoints require auth; unapproved users cannot sign in

---

## 8. Feature dependency & parallelism map

```
Sprint 1:  F11 (Company/Lead + LeadIngestionService + tenant foundation/query filter) ─► F12 (app shell) ─► F13 (leads list/detail) ─► F14 (target profiles + discovery; backend can start right after F11, UI after F13) ─► F15 (CSV backend, reuses F11.4 + F14.0 Hangfire) ─► F16 (import wizard)
           F17 (SendGrid DNS) — start day 1, runs in background all sprint
Sprint 2:  F19 (super admin + workspace creation) ∥ F18 (email sender) ─► F20 (membership/approval) ─► F21 (forgot/reset password);  F22 (campaign domain/API) in parallel
Sprint 3:  F23 (sending engine, needs F18 + F22) ─► F24 (campaigns UI; UI can start against the F22 API at the start of the sprint)
Sprint 4:  F25 (inbound webhook) ─► F26 (classification) ─► F27 (inbox UI; UI can start once F25 entities exist)
Sprint 5:  F28 (events + analytics) ∥ F29 (hardening) ─► F30 (Azure + CI/CD, last feature) ─► F31 (UAT / Gate 2)
```
- F11 comes first: it carries the tenant foundation (`ICurrentWorkspace`/`ITenantEntity`, query filter, isolation tests) and the single lead-ingestion path, so every later entity is tenant-safe from creation; F30 is intentionally last, so nothing is auto-deployed until Sprint 5; F17 (SendGrid DNS) is unaffected, but the `leads` app CNAME waits for F30.
- With 3 devs: Backend = F11/F14/F15/F18/F20/F19/F21/F22/F23/F25/F26/F28; Frontend = F12/F13/F14-UI/F16/F20-UI/F19-UI/F21-UI/F24/F27/F28-UI; DevOps/full-stack = F17/F29/F30/F31. With 2 devs, the backend dev also owns F17/F30 and F29 slips toward UI-light polish — protect F23, F25 and F29.3 from being cut.

---

## 9. Data model delta (Phase 1 → end of Phase 2)

New tables: `Company`, `CsvImportBatch`, `TargetProfile`, `LeadDiscoveryRun`, `RegistrationApproval`, `PasswordResetToken`, `Campaign`, `CampaignStep`, `CampaignEnrollment`, `EmailMessage`, `InboxThread`, `InboxMessage`, `AiGenerationLog` (= the existing `AiUsageLog`, extended — see F23 doc), plus `AuditLogEntry` (F19), `SuppressedEmail` (F23), `InboundQuarantine` (F25), `ProcessedWebhookEvent` (F28) and Hangfire's own schema.
Changed: `Workspace` (+TimeZone, +AdminEmail), `AppUser` (+Status, +PlatformRole, nullable `WorkspaceId`), `Lead` (+CompanyId, unique `(WorkspaceId, Email)`, soft delete, `Source`, `SourceRunId`, `EmailVerificationStatus`). `AppUser.WorkspaceId` unchanged.
All tenant tables implement `ITenantEntity`, carry `WorkspaceId`, and are covered by the EF global query filter (Feature 11).

## 10. API surface delta (all `/api/v1`)

| Area | New in Phase 2 |
|---|---|
| Auth | Existing endpoints; `register` takes `workspaceId` and creates a `PendingApproval` user; `POST /auth/approvals/{token}/approve\|reject`; `POST /auth/forgot-password`, `POST /auth/reset-password`, `POST /auth/change-password`; public rate-limited `GET /workspaces?q=` for the registration dropdown |
| Platform admin | `POST /admin/workspaces`, `GET /admin/workspaces` (`SuperAdmin` role only) |
| Leads | `PUT/DELETE /leads/{id}`, `POST /leads/import`, `POST /leads/import/{batchId}/start`, `GET /leads/import/{batchId}/status`, `GET .../errors`; query params on `GET /leads`; |
| Discovery | `GET/POST/PUT/DELETE /target-profiles`, `POST /target-profiles/from-leads`, `POST /target-profiles/{id}/runs`, `GET /discovery/runs/{id}`, `GET /discovery/credits` |
| Campaigns | `GET/POST /campaigns`, `PUT /campaigns/{id}`, steps CRUD, `POST /campaigns/{id}/enroll|activate|pause`, enrollments list |
| Inbox | `GET /inbox/threads`, `GET /inbox/threads/{id}`, `POST /inbox/threads/{id}/reply`, reclassify |
| Analytics | `GET /analytics/campaigns/{id}/summary`, `GET /analytics/workspace/summary` |
| Public/webhooks | `GET /unsubscribe/{token}`, `POST /webhooks/sendgrid/inbound`, `POST /webhooks/sendgrid/events` |
| Ops | `GET /health`, `/hangfire` (`Hangfire:AdminEmails` only) |

## 11. Package additions

| Project | New packages |
|---|---|
| `ZenLead.Infrastructure` | `Hangfire.Core`, `Hangfire.SqlServer`, `SendGrid`, `Azure.Storage.Blobs`, `Azure.Identity`, `CsvHelper`, `Serilog.*` sinks (Application Insights) |
| `ZenLead.Api` | `Hangfire.AspNetCore`, `Azure.Extensions.AspNetCore.Configuration.Secrets`, `Serilog.AspNetCore`, `Microsoft.ApplicationInsights.AspNetCore`, `AspNetCore.HealthChecks.*` (optional) |
| `ZenLead.Application` | none expected (interfaces only) |
| `ZenLead.Tests` | Testcontainers (SQL Server) or a LocalDB fixture; `Microsoft.AspNetCore.Mvc.Testing` for endpoint tests |
| `ZenLead.Client` | chart library chosen per dataviz guidance (e.g. `ngx-charts`/`chart.js`) — decide in Feature 28 |

## 12. Testing priorities (per CLAUDE.md conventions)

Must-have xUnit coverage: CSV parse/dedup; tenant isolation and registration approval; sequence scheduling (time zones/DST/send window); send idempotency; webhook signature verification and idempotency; structured-AI-output parsing (Web defaults + camelCase fixtures). AI and SendGrid always behind fakes — no real calls in CI. Angular: auth interceptor (existing), import-wizard mapping guess, step schedule display. Manual smoke path before each release.

## 13. Phase 2 risks & mitigations (additions to parent plan §11)

| Risk | Mitigation |
|---|---|
| DNS/MX propagation or SendGrid domain verification delays | Feature 17 in week 4; keep a fallback of testing with SendGrid single-sender verification so Sprint 3 isn't blocked |
| UAT or dev accidentally emails real prospects (imported/discovered leads exist in the DB) | Fail-closed `Sending:Mode=Restricted` + recipient allow-list (F23); `Live` must be set explicitly (startup requires the setting in Production, F30) |
| New domain/IP has poor deliverability; test emails land in spam | Warm-up volume caps per workspace, SPF/DKIM/DMARC set, unsubscribe link, plain-text part; internal UAT uses small volumes |
| Double-sends from Hangfire retries/overlapping runs | Insert-before-send + `DisableConcurrentExecution` on the sender job + DB-level unique constraint on `(EnrollmentId, StepId)` for `EmailMessage` |
| Strangers register to spam the admin's inbox or probe workspace names | Approval needed before any access; per-IP/per-email limits and a pending-request cap per workspace (F20.3); workspace picker returns only id+name, rate-limited; revisit the public picker before onboarding other organisations |
| Super admin account is a high-value target (it can create workspaces; no MFA in Phase 2) and its credentials could leak | Credentials only in user-secrets/Key Vault, strong-password check at seed, lockout + rate limit, audit log on workspace creation, role has **no** access to tenant data (so compromise cannot read leads/mail); MFA added to the backlog; rotate via password reset |
| Password-reset abuse (email bombing, account enumeration, token theft) | Uniform responses, per-email/IP throttling, hashed single-use 1-hour tokens, all sessions revoked on reset, notice email on change; reset link base URL from config only (no Host-header-derived links) |
| Query filter silently hides data in background jobs / webhooks (no HTTP context) | Jobs and webhooks take an explicit `workspaceId`, use a documented `IgnoreQueryFilters()` + explicit filter pattern, and have tests that run a job with no ambient workspace |
| Inbound reply can't be matched to a thread | Three-level matching (Reply-To token → headers → sender email) + quarantine view; never silently drop |
| Prompt injection via reply text/CSV fields feeding the LLM | Delimited input, enum-constrained output, no tool access, adversarial fixtures |
| AI cost overruns with per-step personalisation across thousands of leads | Workspace cap enforced before each call; personalisation optional per step; show estimated cost on activate |
| Deferring Azure to the end: first production run in Sprint 5 exposes integration bugs (Key Vault/managed identity, Azure SQL + Hangfire, Blob, webhook URLs, App Insights), with Gate 2 only days away | Keep prod paths behind interfaces/config from day one; run `dotnet publish` and start the published build locally at least once per sprint; F30.6 parity checklist; protect a buffer in Sprint 5 and cut F29 polish/charts first, not F30 or F31 |
| Sprint 5 is overloaded (F28, F29, F30, F31) | F28 can start in Sprint 4 once F23 sends events; F30.1 (Bicep/`az` scripts) can be written and validated in a throwaway resource group earlier without deploying the app |
| Webhook development without a public URL | Persistent dev tunnel (§1c); fixture-based tests for payloads; secrets/signatures enforced even on the tunnel |
| Azure cost drift above single-org budget | Budget alert (F30.5), B1 plan + Basic/free SQL, App Insights ingestion cap, weekly check in UAT |
| Capacity: 5 sprints is tight | Stretch items (AI reply suggestion, soft-delete UI, charts polish) are first to cut; F23, F25, F29.3 are not |
| Discovery provider cost/quality: credits burn fast, emails are stale or unverified, bounces hurt the new sending domain | Monthly credit cap + per-run cap (F14.6); provider verification flag stored and `Invalid` blocked from enrollment (F14.7); review imported leads before enrolling; provider cost shown in Gate 2 cost audit |
| Provider terms restrict storing/using discovered data or API access tier | Read PDL's terms before the first real run; `ILeadSource` keeps a swap to Apollo/others cheap; fake source means development isn't blocked |
| CSV scale: large files time out/OOM | Streaming parse + batched commits + file/row caps from day one |

## 14. Explicitly out of scope for Phase 2 (don't build yet)

**Deferred multi-tenancy extras (later phase):** roles beyond admin/member; several admins or changing the workspace admin; admin user-management UI (list/disable users); invite-by-admin flow; workspace-switching UI (one user in several workspaces); `PlanTier`/`Country` on `Workspace`; per-workspace sender identity; MFA / SSO; super-admin UI beyond workspace create/list (rename, delete, change admin, suspend). (The tenant query filter, isolation tests, shared-workspace membership and email approval are **in** Phase 2.) The original designs are in this file's git history.

Web scraping for leads; auto-enrolling discovered leads without review; a standalone email-verification vendor integration (unless chosen with the provider); scheduled/recurring discovery runs; phone-number discovery; LinkedIn/WhatsApp/SMS; CRM pipeline/deal stages; Stripe billing/plan enforcement; white-label/custom domains/PDF reports; any RBAC; GDPR/PDPL tooling (beyond unsubscribe/suppression); lead scoring beyond reply classification; CRM integrations/public API; multi-workspace switching UI; SignalR/real-time inbox; branching sequences; A/B testing; calendar/meeting booking; email warm-up automation.

---

*Scoped to Proposal Phase 2, weeks 4–13 of [zen-lead-mvp-implementation-plan.md](zen-lead-mvp-implementation-plan.md). Update §0 "Starting state" and CLAUDE.md "Project state" as each sprint lands.*
