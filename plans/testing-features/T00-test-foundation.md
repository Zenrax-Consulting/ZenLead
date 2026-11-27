# T00 — Test Foundation

**Branch:** `test/foundation` · **Size:** S–M (about a day) · **Blocks:** T02–T10 · **Revisited:** 2026-11-27 against `master` at `f5ad849` (F11–F28 merged). Decisions are recorded in [§ Decisions closed](#decisions-closed-2026-11-27).

## Goal
Add the shared pieces every other workstream needs: test categories, an `E2E` environment with an email pickup sink, deterministic job execution and test-support endpoints, seed scenarios, and test-data builders. Put them in place once, so the parallel lanes don't each build their own.

## What already exists (no work needed)
Checked against the code. T00 relies on these and does not rebuild them:
- `Hangfire:Enabled=false` already swaps in `NoopJobScheduler` and skips the dashboard (`Program.cs`).
- `Sending:Enabled` already controls whether the recurring `campaign-sender` job is registered.
- `RateLimits:*PermitLimit` overrides exist for Auth, Register, Picker, Forgot, Unsubscribe and Reply.
- `Sending:RecipientAllowList` already accepts domains (`example.test` or `@example.test`).
- `TimeProvider` is registered as a singleton (`TimeProvider.System`) and is used by the sending engine, approvals, reset, registration, discovery, CSV import, inbound, classification effects, the event webhook (signature age), analytics, the AI budget and suppression.
- `Seed:SuperAdmin` plus `SuperAdmin:Email/Password` config drives `SuperAdminSeeder`.
- The test project already has `FakeEmailSender`, `FixedTimeProvider`, `TestDb` (SQLite, `EnsureCreated`), `ApiFactory`, `EventWebhookSigner`, `InboundFixtures`, `InboxSeed` and per-area harnesses.

## Files to add / modify

### Backend
| File | Change |
|---|---|
| `ZenLead.Tests/Support/TestCategories.cs` | `public static class TestCategories { Integration, Azurite, Performance, Live }`. Tests use the constants instead of string literals. **Rename the existing `"LiveAi"` trait** (`ReplyClassifierLiveTests`) to `TestCategories.Live`, and point the existing `Azurite` test at its constant. |
| `ZenLead.Tests/Support/Builders/*.cs` | Fluent builders: `WorkspaceBuilder`, `UserBuilder` (status, platform role), `LeadBuilder`, `CompanyBuilder`, `CampaignBuilder` (+ steps), `EnrollmentBuilder`, `EmailMessageBuilder`, `InboxThreadBuilder`. Each builder writes through `TestDb` or `ApiFactory.Services`. Existing harnesses (`CampaignHarness`, `SenderHarness`, `InboxSeed`, …) can move onto them as they're touched, but don't rewrite them in this PR. |
| `ZenLead.Infrastructure/Email/PickupEmailSender.cs` | `IEmailSender` that writes each `OutboundEmail` as `{utc-timestamp}-{guid}.json` (to, from, reply-to, subject, text, html, headers, custom args) to `Email:PickupDirectory`. Registered when `Email:Provider=Pickup`. The file name never comes from message content. |
| `ZenLead.Infrastructure/Testing/RecordingJobScheduler.cs` | `IJobScheduler` used in `E2E` (Hangfire is off there). It records each `EnqueueDiscoveryRun` / `EnqueueCsvImport` / `EnqueueReplyClassification` call in a singleton in-memory queue instead of running it. |
| `ZenLead.Infrastructure/Testing/AdjustableTimeProvider.cs` | `TimeProvider` with `Advance(TimeSpan)` and `SetUtcNow(DateTimeOffset)`, starting at the real time. Singleton, `E2E` only. |
| `ZenLead.Api/StartupConfiguration.cs` | Replace the `bool isDevelopment` parameter with the host environment (`Development`, `E2E`, anything else). Rules: `Pickup` is allowed only in `Development` and `E2E` (as `Log` is only allowed in Development today). In `E2E`: `Email:Provider` must be `Pickup`, `LeadSource:Provider` must be `Fake`, `Ai:Provider` must be `Fake`, `Hangfire:Enabled` must be `false`, `Sending:Mode` must be `Restricted`, and **startup fails if `SendGrid:ApiKey` or `OpenAI:ApiKey` is set**. `OpenAI:ApiKey` is required only when `Ai:Provider` is `OpenAI` (the default). `Ai:Provider=Fake` is refused outside `Development` and `E2E`. |
| `ZenLead.Api/Controllers/V1/Test/TestSupportController.cs` | Removed from the application parts by a `ControllerFeatureProvider` unless `IHostEnvironment.IsEnvironment("E2E")`, so the routes don't exist anywhere else. Anonymous, but rejects any non-loopback `RemoteIpAddress` with 403. Endpoints below. |
| `ZenLead.Api/Program.cs` | In `E2E`: register `AdjustableTimeProvider` as `TimeProvider`, `RecordingJobScheduler` as `IJobScheduler`, `PickupEmailSender`, and `FakeEmailComposer` / `FakeReplyClassifier`. Skip the Semantic Kernel/OpenAI registration when `Ai:Provider=Fake`. |
| `ZenLead.Infrastructure/Ai/FakeEmailComposer.cs`, `FakeReplyClassifier.cs` | Deterministic output keyed on the input, selected by `Ai:Provider=Fake`. Composer: subject and body built from the lead's first name and company plus the step text; fixed token counts. Classifier markers in the reply body: `[[unsubscribe]]` → `Unsubscribe` 0.99, `[[ooo]]` → `OutOfOffice` 0.95, `[[not-interested]]` → `NotInterested` 0.95, `[[review]]` → `Interested` 0.30 (below the review threshold), otherwise `Interested` 0.95. Both still go through `IAiBudget` and write `AiUsageLog` rows, so the budget banner and usage screens work. These are Infrastructure classes, separate from the test-project fakes, which stay as they are. |
| `ZenLead.Api/appsettings.E2E.json` | Non-secret E2E config: `ConnectionStrings:Default` → LocalDB `ZenLeadE2E`, `Email:Provider=Pickup`, `Email:PickupDirectory=../.e2e/mail`, `LeadSource:Provider=Fake`, `Ai:Provider=Fake`, `Hangfire:Enabled=false`, `Sending:Mode=Restricted`, `Sending:RecipientAllowList=["example.test"]`, `Seed:SuperAdmin=false` (the scenarios seed it), `App:PublicBaseUrl=https://localhost:52618`, `Storage:ConnectionString=UseDevelopmentStorage=true`, generous `RateLimits:*`. The runner generates secrets per run and passes them as environment variables: `Jwt__SigningKey`, `Unsubscribe__SigningKey`, `SendGrid__InboundSecret`, `SendGrid__EventWebhook__PublicKey` (the private key stays with the runner for signing), `SuperAdmin__Email`, `SuperAdmin__Password`. |
| `ZenLead.Tests/Api/TestSupportGuardTests.cs` | **Security guard:** under `Development` and `Production`, every `/api/v1/test/*` route returns 404. `Email:Provider=Pickup` or `Ai:Provider=Fake` makes startup fail outside `Development`/`E2E`. Under `E2E`, a non-loopback caller gets 403, and startup fails if a SendGrid or OpenAI key is present. |
| `ZenLead.Tests/Api/StartupConfigurationEnvironmentTests.cs` | The new environment rules above (the existing `StartupConfigurationTests` are unchanged). |
| `.gitignore` | `.e2e/`, `ZenLead.E2E/test-results/`, `ZenLead.E2E/playwright-report/`, `tools/load/results/` |

### Test-support endpoints (`/api/v1/test/*`, E2E only)
| Endpoint | Does |
|---|---|
| `POST /test/reset` | `EnsureDeleted` + `Migrate` the E2E database, clear the pickup folder, clear the recorded job queue, reset the clock to real time. Called once per run from Playwright global setup. Hangfire is off, so nothing else holds the DB open. |
| `POST /test/seed/{scenario}` | Runs a seed scenario (below) and returns its ids, emails and generated passwords as JSON. Safe to call many times without a reset. |
| `GET /test/jobs` | The recorded job queue (type, ids), so a test can assert "a classification was enqueued". |
| `POST /test/jobs/run-pending` | Runs every recorded job, oldest first, each in its own DI scope through the **same job class Hangfire calls** (`ProcessCsvImportJob`, `ProcessLeadDiscoveryJob`, `ClassifyReplyJob`). Jobs enqueued while it runs are drained too. Returns what ran and any exception per job. Optional `?type=csv\|discovery\|classify`. |
| `POST /test/jobs/campaign-sender/run` | Runs `CampaignSenderJob.ExecuteAsync` once, synchronously. The recurring cron is never registered in E2E (Hangfire off). |
| `GET /test/clock` · `POST /test/clock/advance {minutes}` · `POST /test/clock/set {utc}` | Reads and moves `AdjustableTimeProvider`. `GET` lets a test sign Event Webhook payloads with the server's time, so the signature-age check still passes after the clock moves. |

### Seed scenarios (`ZenLead.Infrastructure/Testing/SeedScenarios.cs`, used by the E2E endpoint)
Every call creates **fresh GUIDs**, and workspace names and emails carry a run id (`… <runId>`, `<role>-<runId>@example.test`), so parallel tests never collide and no reset is needed between them. The content is the same every time: names, statuses, sources, step text, and dates relative to the current `TimeProvider` time. Passwords are generated per call and returned in the response. The super admin comes from the `SuperAdmin__*` environment variables and is created once (idempotent).

| Scenario | Content |
|---|---|
| `empty` | Super admin only |
| `gate2` | Super admin, workspace "Zenrax E2E" + admin user, a second workspace "Other Co" + user (for isolation), 25 leads over 3 companies with mixed statuses and sources, one target profile, one draft campaign with 3 steps |
| `inbox` | `gate2` + an active campaign with sent messages, 3 threads (interested / unsubscribe / needs-review), one quarantined inbound |
| `analytics` | `inbox` + email events over the last 30 days, crossing a DST boundary in the workspace time zone |

`bulk:{n}` moved to **T06**: testing `SqlBulkCopy` needs SQL Server, and only T06 uses it.

### Frontend / tooling
| File | Change |
|---|---|
| `ZenLead.Client/src/proxy.conf.js` | No change. `/api/v1/test` is under `/api`, and proxied calls arrive from loopback. |
| `README.md` | Short "E2E environment" section: the env vars the runner sets, that Azurite must be running (CSV upload uses blob storage), and the verification commands below. |
| `docs/testing/README.md` | Short index: how to run each pack, where evidence goes, the tag list. |

## Tests in this PR
- `TestSupportGuardTests` and the `StartupConfigurationTests` additions (above).
- `PickupEmailSenderTests`: writes valid JSON with every field, never writes outside the configured folder (the file name is timestamp + GUID, never taken from the subject), and creates the folder if it's missing.
- `FakeAiTests`: each classifier marker maps to its documented label; composer output is the same for the same input and keeps a personalised step's subject and body.
- `TestSupportEndpointsTests` (the API booted in `E2E` over SQLite by `E2EApiFactory`, with the environment's own registrations):
  - **Seeds:** each scenario runs twice on the same DB without colliding and returns the ids it promises; the returned credentials sign in; the "other" workspace can't read the main workspace's leads; seed dates follow the test clock.
  - **Recorded jobs:** enqueues are recorded, not run; `run-pending` runs classification through the real `ClassifyReplyJob` with the fake classifier and writes `AiUsageLog` rows; a failing job is reported and the others still run; `?type=` leaves other jobs queued.
  - **Sender and pickup:** registering writes the approval email to the pickup folder; `campaign-sender/run` mails a due enrollment there.
  - **Clock:** advance and set move the app's `TimeProvider`.

## Verification
1. With Azurite running and the runner env vars set: `$env:ASPNETCORE_ENVIRONMENT='E2E'; dotnet run --project ZenLead.Api --no-launch-profile`. `POST /api/v1/test/reset`, then `POST /api/v1/test/seed/gate2` returns ids. Register a user, and an approval email JSON appears in `.e2e/mail`.
2. Upload a CSV through the API. `GET /test/jobs` shows one csv job, and `POST /test/jobs/run-pending` imports the rows.
3. Start in `Development` and confirm `/api/v1/test/reset` is a 404.
4. `dotnet test ZenLead.Tests` stays green.

## Not in this feature
- `bulk:{n}` seeding (T06).
- The Playwright project and its fixtures (T07). T00 provides only the server side they call.
- Converting the remaining `DateTime.UtcNow` call sites to `TimeProvider` (see below).

## Known limits (accepted)
- **Clock coverage.** `AdjustableTimeProvider` moves everything listed under "What already exists". It does **not** move JWT expiry or refresh-token expiry (`JwtTokenGenerator`, `RefreshTokenService`), or the `CreatedAt`/`UpdatedAt` stamps in `LeadIngestionService`, `EfTokenUsageTracker`, several repositories, `AppUser` and `TargetProfilesController`. E2E flows that advance the clock (follow-up steps days apart) still work, because sending, scheduling and webhooks use `TimeProvider`. Tests must not assert on those stamps after advancing the clock. Logged in `docs/parking-lot.md` as a follow-up.
- **Azurite is required** for E2E CSV-upload flows. T01's `start-deps.ps1` starts it.

## Decisions closed (2026-11-27)
| # | Question | Decision |
|---|---|---|
| T00-1 | How jobs run in E2E | Hangfire off (`Hangfire:Enabled=false`). `RecordingJobScheduler` records enqueues; tests drain them with `/test/jobs/run-pending` and run the sender with `/test/jobs/campaign-sender/run`. This replaces the old `classify-pending` and per-id job routes. |
| T00-2 | Seed ids | Fresh GUIDs and run-id names on every call. Same content every time; ids and passwords are returned. No reset between tests. |
| T00-3 | `bulk:{n}` | Moved to T06. |
| T00-4 | OpenAI key in E2E | New `Ai:Provider` switch (`OpenAI` by default, `Fake` in Development/E2E only). E2E refuses to start with a real OpenAI or SendGrid key. |
| T00-5 | Trait names | `LiveAi` becomes `Live`. All traits use the `TestCategories` constants. |
| T00-6 | Clock scope | Only code that uses `TimeProvider` moves with the test clock. The remaining `UtcNow` call sites are a parking-lot follow-up, not T00 work. |
