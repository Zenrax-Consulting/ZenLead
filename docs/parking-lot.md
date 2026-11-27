# Parking lot

Pending items left after a feature's implementation. Add a section per feature; remove items when they are done.

## Feature 11 — Company, lead model, ingestion service & tenant foundation

Items below come from the plan's Verification and "Not in this feature" sections. The repo doesn't show whether the manual checks were run, so they are listed as unconfirmed.

### Unconfirmed manual checks
- [x] Run the `AddCompanyAndLeadProvenance` migration against a LocalDB holding Phase 1 leads, including a mixed-case duplicate email pair, to confirm it fails loudly and then succeeds after the fix. Automated by T03 (`MigrationTests.Phase1Database_…`).
- [ ] Two-workspace check via `ZenLead.Api.http`: B gets 404 on A's lead (GET and DELETE), duplicate email gives 409, `New` → `Contacted` gives 200 and back to `New` gives 409.
- [ ] Generate a draft from lead detail for a lead with a company and confirm the prompt includes the company.

### Deferred by the plan
- [ ] Restore-deleted-lead action (soft-deleted leads still block re-adding the same email).

## Feature 12 — App shell

### Unconfirmed manual checks
- [ ] Log in and check the shell: four nav items, workspace name top-right.
- [ ] Deep link `/leads/<id>` while logged out redirects to `/login`.
- [ ] Below 800 px the hamburger menu appears; Campaigns, Inbox and Analytics show the placeholder.
- [ ] Refresh on `/leads` stays logged in.

### Follow-ups owned by later features
- [ ] F19: hide tenant nav for the super admin (role-aware navigation).
- [ ] F20: when the anonymous workspace picker is added to the workspaces controller, put `[Authorize]` on the actions, not the class.
- [ ] F29: allow-list `fonts.googleapis.com` / `fonts.gstatic.com` in the CSP, or self-host the Material icon font.
- [ ] Real Campaigns, Inbox and Analytics screens replace the placeholders (F24, F27, F28); breadcrumbs and theming changes are out of scope.

## Feature 13 — Leads UI: list & detail

### Unconfirmed manual checks
- [ ] Seed about 50k leads in a dev DB and confirm: list loads in under 1 s, page 2000 works. (The rest — every column sorts stably, `q=acme` matches name/email/company, `q=%` / `_` / `[` match literally — is automated on SQL Server by T03 `LeadSearchTests`; the 50k timing moves to T06.)
- [ ] Copy the list URL into a new tab and see the same view.
- [ ] Select leads across two pages, open a lead, go back: selection is intact.

### Follow-ups owned by later features
- [x] F16: add the "Import CSV" button/CTA on the list and empty state.
- [ ] F24: fill the "Add to campaign" selection-bar action and flip `features.campaigns`.
- [ ] F29.5: re-measure name search (`LIKE '%x%'` scans); move to a full-text index only if it is too slow.
- [ ] Status history card is a placeholder until F25 events exist.
- [ ] Out of scope: saved views, column chooser, export.

## Feature 14 — Automated lead discovery

### Before the first real run (Gate 2 prerequisite)
- [ ] Do one real discovery run against PDL's free/test tier (`LeadSource:Provider=Pdl`, `LeadSource:Pdl:ApiKey`). `PdlLeadSource` is only tested against a stubbed HTTP handler.
- [ ] Verify PDL request/response details against the account tier: the `work_email` exists filter, the company-size buckets, the optional `email_status` field (the search docs don't show one; it falls back to `Unverified`), and that credits are charged per returned record.
- [ ] Re-read PDL's terms on storing and using returned data (parent plan §1).

### Not verified
- [ ] Manual UI pass in a browser: profiles list, editor chips, run dialog polling, "Create target profile" from the lead list and lead detail, "Job dashboard" menu item. Only unit tests ran.
- [ ] Kill the API mid-run and restart, to confirm Hangfire resumes the run without duplicates (plan verification step).
- [ ] Run twice with `Fake` and confirm the second run is mostly duplicates; set `MonthlyCreditCap` to 10 and confirm the run ends as Cap reached.
- [ ] Confirm the Job dashboard opens for an email in `Hangfire:AdminEmails` (the cookie flow end to end; the `window.open` after the POST may be blocked by some popup blockers).

### Known gaps / follow-ups
- [ ] `GetRemainingCreditsAsync` returns `null` for PDL, so the credits endpoint shows local usage only. Needs PDL's account usage endpoint or tier.
- [ ] A run can import up to 5 more leads than requested (documented overshoot rule). Slice to the remaining count before ingesting if exact counts matter.
- [ ] If the process dies after a page is ingested but before the run counters are saved, the resumed run re-fetches that page and counts its leads as duplicates, so `ImportedCount` is undercounted.
- [ ] Rows the provider marks `Invalid` are dropped by ingestion and appear in no run counter (not in imported, duplicates, suppressed or no-email).
- [ ] A run left in `Running` by a crash blocks new runs until Hangfire retries it; there is no stale-run timeout.
- [ ] The "only leads that replied" checkbox only knows statuses of leads on the current list page.
- [ ] Add an `ApolloLeadSource` (or other vendor) as a sibling class plus one registry entry, if wanted.
- [ ] Hangfire `QueuePollInterval` is 15 s for the free-tier DB; re-check with Azure SQL auto-pause in F30. Data Protection key ring is per-machine; configure persistent keys in Azure (F30).
- [ ] No Angular component-render tests for the profiles list; only the service, editor and run dialog logic are covered.

### Explicitly out of scope (per the plan)
Scheduled/recurring runs, auto-enrolment of discovered leads, a standalone email verifier (`IEmailVerifier`), phone discovery, resuming a failed run from the UI, per-lead enrichment calls.

### Housekeeping
- [ ] Open the PR for `feature/lead-discovery` into `master`.
- [ ] Update the "current state" section in `plans/` and `CLAUDE.md` project state to include F14.

## Feature 15 — CSV import backend

### Not verified
- [ ] Run the plan's Verification end to end with Azurite and the API: messy 5-10k row file (`;` variant, Latin-1 export, duplicates, an unsubscribed lead), `start`, poll `status` to Completed, counts reconcile, job visible at `/hangfire`, errors file opens in Excel with no formulas executing, same file again imports 0.
- [ ] Kill the API mid-import and restart, to confirm the job resumes from the checkpoint and the final count equals the file's valid unique rows.
- [x] Run the Azurite tests against a real Azurite; confirms `BlobClient.OpenReadAsync` returns a seekable stream (the reader copes either way by buffering). Now `dotnet test ZenLead.IntegrationTests --filter Category=Azurite` (T03), which also streams a 10 MB file and runs a CSV import through the API and Hangfire.

### Known gaps / follow-ups
- [ ] No startup check for `Storage:ConnectionString` / `Storage:AccountUri` outside Development; with neither set it falls back to the Azurite connection string. Decide whether `StartupConfiguration` should fail fast in production (F30).
- [ ] A crash between ingesting a batch and saving its counters means the retry counts that batch as duplicates, so `ImportedCount` is undercounted (same as F14).
- [ ] A stray quote in an unquoted field (e.g. `5" pipe`) is reported as "Malformed quoting" rather than imported; tolerance can be loosened if real files need it.
- [ ] Old blobs are never deleted; relies on the F30 lifecycle rule.
- [x] F16: port the `ColumnGuesser` synonym table to the wizard; add the "Import CSV" CTA (see F13 follow-up).

## Feature 16 — Import wizard

### Not verified
- [ ] Run the plan's manual Verification against the running API + Azurite: 10k-row `;` file → mapping auto-filled, missing-email error blocks Continue, start → bar advances, leave to `/leads` and come back with browser Back (`?batch=` resumes), result counts match the server, issues CSV downloads and opens cleanly, **View imported leads** shows only that batch's rows.
- [ ] Look at the stepper in the browser: drag-and-drop, header states while going back from Map/Review, and the layout on a narrow screen.

### Known gaps / follow-ups
- [ ] Initial bundle was already over the 1 MB error budget on `master` (by ~1 kB) because `SharedModule` (every Material module) is imported eagerly by `AppModule`. F16 raised the error budget to 1.1 MB and kept `MatStepperModule` in `LeadsModule` (lazy). Proper fix: give the shell its own small module list and stop importing `SharedModule` in `AppModule`, then put the budget back to 1 MB.
- [ ] Resuming an `Uploaded` (never started) batch asks the user to upload the file again; resuming at the mapping step would need a "get headers" endpoint.
- [ ] Only component logic is unit-tested (constructed without TestBed, as in the run-discovery dialog); there is no template-render test for the stepper.
- [ ] A 409 on start (batch already started, e.g. double submit in two tabs) shows the message but doesn't jump to the progress step.

### Explicitly out of scope (per the plan)
Saved mapping templates, re-mapping a started import, Excel files, an import history list, drag-reordering of columns.

## Testing (T00 test foundation)

### Known gaps / follow-ups
- [ ] Several call sites still use `DateTime.UtcNow` instead of `TimeProvider`: JWT expiry (`JwtTokenGenerator`), refresh tokens (`RefreshTokenService`), and `CreatedAt`/`UpdatedAt` stamps in `LeadIngestionService`, `EfTokenUsageTracker`, `CampaignRepository`, `CsvImportRepository`, `LeadIngestionStore`, `LeadRepository`, `TargetProfileRepository`, `WorkspaceRepository`, `AppUser` and `TargetProfilesController`. The E2E test clock (T00) doesn't move them, so E2E tests must not assert on these stamps after advancing the clock. Move them to `TimeProvider` when they're next touched (candidate for F29).

## Testing (T03 integration)

### Known gaps / follow-ups
- [ ] **SendGrid 5xx and client timeouts are retried, so a lead can get the same step twice.** `SendGridEmailSender` maps 429, 5xx and timeouts to a retryable failure (as the F18 plan says); `SendDueEmailsUseCase` then deletes the `Queued` row and retries after the backoff. A 5xx or timeout *after* SendGrid accepted the request means the email may already be out. T03's spec wanted those treated as "uncertain" (failed, never resent), matching the at-most-once rule for crashes. Tested as built (`SendGridSenderTests.ClientTimeoutAfterSendGridAccepted_…_KnownGap`): decide in F29 whether 5xx/timeouts should become `Failed("Uncertain…")` and only 429 / connection refused stay retryable.
- [ ] **T00 seed templates use `{{first_name}}`, which is not a token** (`TemplateRenderer` knows `{{firstName}}`, `{{lastName}}`, `{{name}}`, `{{company}}`, `{{title}}`). `SeedScenarios` step bodies and the `CampaignBuilder.WithStep` defaults are sent literally ("Hi {{first_name}},"). Switch them to `{{firstName}}`.
- [ ] Re-run T02's `[Trait("Stack","Both")]` contract tests on `IntegrationApiFactory` once T02 is merged (T03 ships its own API-on-SQL-Server suite meanwhile).
- [ ] `ZENLEAD_TEST_SQL=container` (Testcontainers SQL Server) is implemented but has not run: no Docker on the dev machine. First run is part of F30's CI work.

