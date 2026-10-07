# Phase 2 — Per-Feature Detailed Plans

File-by-file implementation plans for each feature in [phase-2-mvp-implementation-plan.md](../phase-2-mvp-implementation-plan.md) (Features 11–31). Same format as [phase-1-features/](../phase-1-features/README.md): each doc lists the files to add/modify and the code to put in them. Read the parent plan first for goals, locked decisions and the sprint order — these docs assume it.

Written against the code as it stood on `master` at commit `a104fbe` (2026-10-07). Code blocks are the intended shape, not paste-and-forget: where a doc says **verify**, the detail depends on an installed package version or a Phase 1 file that may have changed — check it when you start the feature. Each feature is one branch + PR, merged in the order below.

| # | Feature | Branch | Sprint |
|---|---|---|---|
| 11 | [Company, Lead Model, Ingestion & Tenant Foundation](11-company-lead-ingestion-tenancy.md) | `feature/company-lead-extension` | 1 |
| 12 | [App Shell](12-angular-shell.md) | `feature/angular-shell` | 1 |
| 13 | [Leads UI: List & Detail](13-leads-ui-list-detail.md) | `feature/angular-leads-list` | 1 |
| 14 | [Automated Lead Discovery](14-lead-discovery.md) | `feature/lead-discovery` | 1 |
| 15 | [CSV Import Backend](15-csv-import-backend.md) | `feature/csv-import-backend` | 1 |
| 16 | [Import Wizard UI](16-import-wizard.md) | `feature/angular-import-wizard` | 1 |
| 17 | [SendGrid Domain & Inbound DNS](17-sendgrid-dns.md) | `chore/sendgrid-dns` | 1 |
| 18 | [Transactional Email Sender](18-email-sender.md) | `feature/email-sender` | 2 |
| 19 | [Super Admin & Workspace Creation](19-super-admin-workspaces.md) | `feature/super-admin-workspaces` | 2 |
| 20 | [Workspace Membership & Registration Approval](20-workspace-membership.md) | `feature/workspace-membership` | 2 |
| 21 | [Forgot & Reset Password](21-password-reset.md) | `feature/password-reset` | 2 |
| 22 | [Campaign Domain & Builder API](22-campaigns-backend.md) | `feature/campaigns-backend` | 2 |
| 23 | [Sending Engine](23-sending-engine.md) | `feature/sendgrid-sending-engine` | 3 |
| 24 | [Campaigns UI](24-angular-campaigns.md) | `feature/angular-campaigns` | 3 |
| 25 | [Inbound Parse Webhook & Threading](25-inbound-webhook.md) | `feature/inbound-webhook` | 4 |
| 26 | [AI Reply Classification](26-reply-classification.md) | `feature/reply-classification` | 4 |
| 27 | [Inbox API & UI](27-inbox-ui.md) | `feature/inbox-ui` | 4 |
| 28 | [Event Webhook & Analytics](28-analytics.md) | `feature/analytics` | 5 |
| 29 | [Hardening](29-hardening.md) | `feature/mvp-hardening` | 5 |
| 30 | [Azure Provisioning & CI/CD](30-azure-cicd.md) | `feature/azure-cicd` | 5 |
| 31 | [UAT & Gate 2 Readiness](31-uat-gate2.md) | `chore/uat-gate2` | 5 |

---

## Conventions used by every doc

These are the cross-feature contracts. They are defined once (in the feature named) and every later doc relies on them, so changing one means changing the docs that use it.

### Tenancy (defined in F11)
- Every tenant table implements `ITenantEntity { Guid WorkspaceId }` (Domain). `ZenLeadDbContext` applies a **named** global query filter `"Tenant"` (`WorkspaceId == ICurrentWorkspace.WorkspaceId`) and stamps `WorkspaceId` on insert.
- `ICurrentWorkspace` (Application) is read from the JWT `workspace_id` claim in HTTP and is **null** in Hangfire jobs and webhooks. A null workspace makes the filter match nothing, so a code path that forgets to scope returns *no data* rather than someone else's.
- Jobs and webhooks take `workspaceId` as an argument and query through `db.Set<T>().ForWorkspace(workspaceId)` (`TenantQueryExtensions`, F11) — `IgnoreQueryFilters(["Tenant"])` + explicit `WorkspaceId ==`. Soft-delete filters stay on.
- Controllers keep the Phase 1 manual `workspace_id` check (defence in depth). Every new tenant endpoint gets one cross-workspace test.

### Layering reminders
- Interfaces live in `ZenLead.Application/Abstractions`; implementations in `ZenLead.Infrastructure`; fakes in `ZenLead.Tests`. Application never references SendGrid, OpenAI/Semantic Kernel, Hangfire, Azure or EF types.
- Hangfire job classes live in `ZenLead.Infrastructure/Jobs`; they are plain classes with a public `ExecuteAsync(...)` taking only primitive arguments, so tests call them directly and Hangfire can serialise them.
- Use cases are `public class XxxUseCase` registered `AddScoped` in `Program.cs` (existing pattern). Controllers stay thin: validate (FluentValidation), call, map to HTTP.
- New secrets/config keys are added to `StartupConfiguration.RequiredKeys` only when the app cannot start without them in every environment; feature-specific keys (SendGrid, lead source) are validated by the feature's own `Add…` extension so the app still boots for local work without them.

### Angular reminders
- NgModule app, components declared in `app-module.ts` with `standalone: false`; **zoneless** — every async state change needs `cdr.markForCheck()` (see `register.ts`).
- Features declare their own `NgModule` and are **lazy-loaded** from F12 on (`loadChildren`), so `app-module.ts` stops growing. Material modules are imported in `shared/shared-module.ts` (created in F12).
- API clients are `@Injectable({ providedIn: 'root' })` services next to their feature, hand-written types in `*.models.ts`, paths always `/api/v1/...`. Any path **outside** `/api` (only `/hangfire` and `/unsubscribe` pages, if proxied) must be added to `src/proxy.conf.js`.

### Migrations
One migration per feature that changes the schema, named in the doc. Generate with
`dotnet ef migrations add <Name> -p ZenLead.Infrastructure -s ZenLead.Api`; read the generated `Up()` before applying (data fix-ups are hand-added where the doc says so). Never edit a migration that has been merged.

### Tests
xUnit, no real OpenAI/SendGrid/lead-provider calls ever. DB-touching tests use the shared `TestDb` helper from F11 (SQLite in-memory, real EF model incl. query filters) rather than the EF InMemory provider, which does not enforce unique indexes.

---

## Deviations from the parent plan

Decisions these docs make that differ from (or add to) wording in [phase-2-mvp-implementation-plan.md](../phase-2-mvp-implementation-plan.md). Each is argued in the feature doc named; none changes a Gate 2 criterion. Fold them into the parent plan once confirmed.

| # | Parent plan says | These docs do | Where |
|---|---|---|---|
| 1 | `Campaign.FromSenderId` (PBI 22.1/17.3) | One configured sender; `Campaign.FromName` optional; no `SenderIdentity` table | [17](17-sendgrid-dns.md), [22](22-campaigns-backend.md) |
| 2 | `IEmailSender.SendCampaignEmailAsync` (PBI 23.1) | Single `SendAsync(OutboundEmail)` with optional campaign fields | [18](18-email-sender.md) |
| 3 | New `AiGenerationLog` table (PBI 23.3) | The Phase 1 `AiUsageLog` table already exists — **extended** (purpose, campaign, enrollment), not replaced | [23](23-sending-engine.md) |
| 4 | Data model lists 13 new tables | Also adds `AuditLogEntry` (F19), `SuppressedEmail` (F23), `InboundQuarantine` (F25), `ProcessedWebhookEvent` (F28) | [19](19-super-admin-workspaces.md), [23](23-sending-engine.md), [25](25-inbound-webhook.md), [28](28-analytics.md) |
| 5 | Hangfire dashboard "restricted to `Hangfire:AdminEmails`" | Bearer tokens aren't sent on browser navigation, so access is a short-lived HttpOnly cookie issued by an authenticated ops endpoint; `/hangfire` **is** added to `proxy.conf.js` | [14](14-lead-discovery.md) |
| 6 | Reply labels: interested / not_interested / unsubscribe | Adds `out_of_office` (PBI 25.4 already implies it); auto-actions only above a confidence threshold | [26](26-reply-classification.md) |
| 7 | Unsubscribe via `GET /unsubscribe/{token}` | Email link opens a landing page that `POST`s (prefetching scanners can't unsubscribe people); `List-Unsubscribe-Post` one-click uses the same POST | [23](23-sending-engine.md) |
| 8 | PBI 27.3: add `/api/v1/inbox` + `/api/v1/webhooks` to the proxy | Not needed — the existing `/api` context covers them | [27](27-inbox-ui.md) |
| 9 | "`dotnet publish` triggers `ng build` into `wwwroot`" | Not true in the repo today (`ShouldRunBuildScript=false`, no copy step, no CI); an explicit build-client-then-publish step is added | [30](30-azure-cicd.md) |
| 10 | Template tokens `{{firstName}}`, `{{company}}`, `{{title}}` | Also `lastName`, `name`, and `{{token\|fallback}}` syntax; leads missing a required value are skipped, not mailed "Hi ," | [22](22-campaigns-backend.md) |
| 11 | Chart library chosen in F28 | In-house SVG components (trend line + bar list) with table fallbacks; no dependency | [28](28-analytics.md) |
| 12 | Sending retries "with backoff" | Explicit **at-most-once** policy: uncertain sends are skipped, never repeated | [23](23-sending-engine.md) |

## Cross-feature touchpoints (later features that edit earlier features' files)

When a feature's PR touches these, run the earlier feature's tests too.

| Feature | Edits | Why |
|---|---|---|
| F14 | F12 `Shell` (ops menu item), F13 leads list/detail (selection actions) | Job dashboard access, create-profile entry points |
| F18 | F14 `OpsController` (test-email), F12 `WorkspacesController` (time zone) | Proves outbound email; send windows need a zone |
| F19 | F12 `Shell`/routing (role-aware), Phase 1 auth use cases + `JwtTokenGenerator` (nullable workspace, role), F14/F18 `OpsController` (`AnyUser` policy) | Default policy now denies workspace-less tokens |
| F20 | F12 `WorkspacesController` (anonymous picker), Phase 1 register/login/refresh + Angular register/login | Approval flow replaces workspace-per-user registration |
| F21 | F20 `IIdentityService`/`TokenHasher`, Angular login/shell | Reset/change password |
| F23 | F11 `LeadIngestionService` + F22 `EnrollLeadsUseCase` (suppression list), Phase 1 `ComposeEmailUseCase`/`AiController` (budget), `AiUsageLog` | One suppression source of truth; AI cap |
| F25 | F23 `SendDueEmailsUseCase`/`ISenderStore` (outbound threading) | Thread holds the whole conversation |
| F26 | F25 `ProcessInboundEmailUseCase` (enqueue hook), F22 `EnrollmentRules`, `EmailComposer` (shared helper refactor) | Async classification and OOO resume |
| F28 | F23 `EmailMessage` rules, F25 level-2 matching (`SmtpMessageId`) | Events complete the message lifecycle |
| F29 | Logging in Phase 1 `EmailComposer`, `proxy`-adjacent CSP/headers, F19 authorization test, F14/F18 ops | Hardening sweep |
| F30 | `Program.cs` config sources, `EfUnitOfWork`, `StartupConfiguration`, `index.html` (self-hosted icon font), `CLAUDE.md` publish description | Production-only paths |

## Decisions still needed from the project owner

| Decision | Needed by | Default assumed in the docs |
|---|---|---|
| Lead provider: Apollo.io vs People Data Labs (parent §1) | Before the F14 provider class (Sprint 1) | `FakeLeadSource` until decided |
| Sender identity model (F17 §6): single Zenrax sender, `outreach@leads.zenraxconsultancy.com` | Before F22 | Confirmed-as-proposed |
| Inbound Parse mode: parsed fields vs raw MIME (+ MimeKit) | After the first real replies in F25 | Parsed fields |
| Azure SQL: free serverless offer vs Basic | F30.6 (decided by a week of vCore-seconds data) | Free offer first |
| `Sending:RecipientAllowList` during UAT | F31.0 | Enabled in production for the UAT window |
