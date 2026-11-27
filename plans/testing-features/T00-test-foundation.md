# T00 — Test Foundation

**Branch:** `test/foundation` · **Size:** S (about half a day) · **Blocks:** T02–T10

## Goal
Add the shared pieces every other workstream needs: test categories, an `E2E` environment with an email pickup sink and test-support endpoints, a deterministic seed, and test-data builders. Put them in place once, so the parallel lanes don't each build their own.

## Files to add / modify

### Backend
| File | Change |
|---|---|
| `ZenLead.Tests/Support/TestCategories.cs` | `public static class TestCategories { Integration, Azurite, Performance, Live }`. Tests use the constants instead of string literals. |
| `ZenLead.Tests/Support/Builders/*.cs` | Fluent builders: `WorkspaceBuilder`, `UserBuilder` (status, platform role), `LeadBuilder`, `CompanyBuilder`, `CampaignBuilder` (+ steps), `EnrollmentBuilder`, `EmailMessageBuilder`, `InboxThreadBuilder`. Each builder writes through `TestDb` or `ApiFactory.Services`. Existing harnesses (`CampaignHarness`, `SenderHarness`) can move onto them as they're touched, but don't rewrite them in this PR. |
| `ZenLead.Infrastructure/Email/PickupEmailSender.cs` | `IEmailSender` that writes each `OutboundEmail` as `{timestamp}-{guid}.json` (to, from, subject, text, html, headers, custom args) to `Email:PickupDirectory`. Registered when `Email:Provider=Pickup`. |
| `ZenLead.Api/StartupConfiguration.cs` | `Pickup` is allowed only in `Development` and `E2E`, just as `Log` is allowed only in Development today. `E2E` requires `Email:Provider=Pickup` and `LeadSource:Provider=Fake`, and refuses to start with a SendGrid key present. |
| `ZenLead.Api/Controllers/V1/Test/TestSupportController.cs` | Mapped **only when `IHostEnvironment.IsEnvironment("E2E")`** (conditional `MapControllers` filter or a convention that removes the controller otherwise). Anonymous, but bound to loopback (rejects non-loopback `RemoteIpAddress`). Endpoints: `POST /api/v1/test/reset` (drop + migrate the E2E DB, clear the pickup folder), `POST /api/v1/test/seed/{scenario}` (see the seed section below), `POST /api/v1/test/jobs/{campaign-sender\|classify-pending\|csv/{batchId}\|discovery/{runId}}/run` (runs the job class synchronously in a scope, same class Hangfire calls), `POST /api/v1/test/clock/advance {minutes}` (moves an `E2E`-only `AdjustableTimeProvider`). |
| `ZenLead.Api/Program.cs` | Register `AdjustableTimeProvider` as `TimeProvider` in `E2E` only. Disable the recurring Hangfire sender job in `E2E` (tests trigger it explicitly, so runs are deterministic). |
| `ZenLead.Api/appsettings.E2E.json` | Non-secret E2E config: LocalDB `ZenLeadE2E`, `Email:Provider=Pickup`, `Email:PickupDirectory=../.e2e/mail`, `LeadSource:Provider=Fake`, `Sending:Mode=Restricted`, `Sending:RecipientAllowList=["example.test"]`, the composer/classifier fakes switched on, generous rate limits except where a security spec lowers them through config. Generated test signing keys go in environment variables set by the runner, not in this file. |
| `ZenLead.Application/Abstractions` | Nothing new. The fake composer and classifier already exist in tests; add DI switches `Ai:Provider=Fake` (Infrastructure `FakeEmailComposer` / `FakeReplyClassifier` returning deterministic output keyed on input, e.g. a reply containing `[[unsubscribe]]` → Unsubscribe 0.99). |
| `ZenLead.Tests/Api/TestSupportGuardTests.cs` | **Security guard:** under `Development` and `Production`, every `/api/v1/test/*` route returns 404, and `Email:Provider=Pickup` makes startup fail. Under `E2E`, a non-loopback caller gets 403. |
| `.gitignore` | `.e2e/`, `ZenLead.E2E/test-results/`, `ZenLead.E2E/playwright-report/`, `tools/load/results/` |

### Seed scenarios (`ZenLead.Infrastructure/Testing/SeedScenarios.cs`, used by the E2E endpoint and the T06 seeding tool)
| Scenario | Content |
|---|---|
| `empty` | Super admin only (credentials from runner env vars) |
| `gate2` | Super admin, workspace "Zenrax E2E" + admin user, a second workspace "Other Co" + user (for isolation), 25 leads over 3 companies with mixed statuses and sources, one target profile, one draft campaign with 3 steps |
| `inbox` | `gate2` + an active campaign with sent messages, 3 threads (interested / unsubscribe / needs-review), one quarantined inbound |
| `analytics` | `inbox` + email events over 30 days crossing a DST boundary |
| `bulk:{n}` | `n` leads (up to 50k), `n/10` companies, for T06 (bulk insert via `SqlBulkCopy` on SQL Server, batched `AddRange` otherwise) |

All scenarios are deterministic (fixed GUID seed and fixed dates relative to the adjustable clock) and return the ids they created as JSON, so tests don't have to query for them.

### Frontend / tooling
| File | Change |
|---|---|
| `ZenLead.Client/src/proxy.conf.js` | No change (`/api/v1/test` is under `/api`). |
| `docs/testing/README.md` | Short index: how to run each pack, where evidence goes, the tag list. |

## Tests in this PR
- `TestSupportGuardTests` (above).
- `PickupEmailSenderTests`: writes valid JSON, never writes outside the configured folder (path is a GUID, not derived from the subject), and the folder is created if missing.
- `SeedScenariosTests` (SQLite): each scenario runs twice without failing (idempotent after reset) and returns the ids it promises.

## Verification
1. `$env:ASPNETCORE_ENVIRONMENT='E2E'; dotnet run --project ZenLead.Api --no-launch-profile`, then `POST /api/v1/test/reset` and `POST /api/v1/test/seed/gate2` return ids. Register a user and an approval email JSON appears in `.e2e/mail`.
2. Start in `Development` and confirm `/api/v1/test/reset` is a 404.
3. `dotnet test ZenLead.Tests` stays green.
