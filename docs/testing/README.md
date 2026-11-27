# Testing

How ZenLead is tested, and where things go. The plan is [plans/testing-plan.md](../../plans/testing-plan.md); each workstream (T00–T10) has its own doc in [plans/testing-features/](../../plans/testing-features/README.md).

## Run

| What | Command | Needs |
|---|---|---|
| Backend unit + API tests | `dotnet test ZenLead.Tests` | nothing (SQLite in memory) |
| Angular unit tests | `cd ZenLead.Client; npm test` | Node |
| Live AI check | `$env:RUN_LIVE_AI='1'; dotnet test ZenLead.Tests --filter Category=Live` | a real `OpenAI:ApiKey` (costs money) |
| Integration (T03) | `dotnet test ZenLead.IntegrationTests` | LocalDB. Azurite for the blob tests (they skip with a message without it) |
| Integration on Docker SQL Server | `$env:ZENLEAD_TEST_SQL='container'; dotnet test ZenLead.IntegrationTests` | Docker (the F30 Linux CI path) |
| Azurite tests only | `dotnet test ZenLead.IntegrationTests --filter Category=Azurite` | Azurite running |
| E2E / browser (T07, T08) | the API in the `E2E` environment (see the root README) plus the Playwright suite | LocalDB, Azurite (arrives with T07) |
| Packs (T09) | `tools/test-runner/run.ps1 -Pack smoke\|regression\|release` | arrives with T09 |

## Integration tests (T03)

`ZenLead.IntegrationTests/` runs our code against real infrastructure: SQL Server (LocalDB by default), Hangfire on SQL Server storage, Azurite, and WireMock stubs of SendGrid, OpenAI and PDL. It is a separate project, so `dotnet test ZenLead.Tests` stays fast and offline; every class carries `Category=Integration` (a guard test enforces it). About 100 tests, under a minute on a dev laptop.

Fixtures in `ZenLead.IntegrationTests/Support/`, reusable by T05, T06 and T10:

| Fixture | What it gives you |
|---|---|
| `SqlServerFixture` | A `ZenLeadIT_{guid}` database per test class, schema from the **migrations**, dropped afterwards. `ResetAsync()` empties every table with Respawn (keeps `__EFMigrationsHistory` and `HangFire.Schema`). `CreateContext(workspaceId)`, `CreateHangfireStorage()`. `ZENLEAD_TEST_SQL=container` uses one Testcontainers SQL Server for the run. Databases left by a crashed run are dropped after 6 hours. |
| `IntegrationApiFactory` | The API over that database: Hangfire enabled (no in-host server, no recurring sender), the real SendGrid / Semantic Kernel / PDL clients pointed at `ProviderStubs`, a movable `Clock`. Call `ResetAsync()` at the start of each test. |
| `HangfireTestServer` | A real `BackgroundJobServer` (1 worker by default) on a test database, jobs activated from DI. `WaitForJobAsync`, `WaitForIdleAsync`, `FailureOf`; retries off by default, or a given count, or the app's own policy. |
| `ProviderStubs` | WireMock SendGrid (202 + `X-Message-Id`; 4xx/5xx/slow), OpenAI (chat completions with `usage`; failures, timeout-once) and PDL (two scroll pages; 402/429). Every request is recorded. |
| `AzuriteFixture` | Skips (`SkippableFact`) with a clear message when Azurite isn't reachable. `ZENLEAD_TEST_AZURITE` overrides the connection string. |
| `SendingSeed`, `TenantRowSeed` | A due enrollment on an always-open campaign; one row of every tenant entity, with the tenant-filter checks written against a context factory (so T10 can run them on SQLite). |

## Tags

- **xUnit** `[Trait("Category", TestCategories.X)]`: `Integration`, `Azurite`, `Performance`, `Live`. Untagged means unit, and runs by default. `Live` never runs in a default command.
- **Playwright** tags in test titles: `@smoke`, `@regression`, `@critical`, `@a11y`, `@visual`, plus a feature tag `@F11`…`@F28` / `@P1`. A test can carry several.

## Test data

- Builders in `ZenLead.Tests/Support/Builders/` (`WorkspaceBuilder`, `UserBuilder`, `LeadBuilder`, `CompanyBuilder`, `CampaignBuilder`, `EnrollmentBuilder`, `EmailMessageBuilder`, `InboxThreadBuilder`).
- E2E seed scenarios (`empty`, `gate2`, `inbox`, `analytics`) through `POST /api/v1/test/seed/{scenario}`. Each call creates fresh ids, so tests can run in parallel without a reset.
- Emails are `<purpose>-<id>@example.test` (never deliverable). No fixed passwords in the repo: `ApiFactory.NewPassword()` in C#, a per-run password in TypeScript.

## Rules

- No real OpenAI, SendGrid or PDL call outside a `Live` test or a recorded manual check.
- Any config that selects SendGrid also sets `Sending:Mode=Restricted` with an allow-list of tester mailboxes.
- Every new tenant endpoint test file has a case where workspace B tries workspace A's resource.

## Evidence

Manual and scripted checks record their output under `docs/testing/evidence/<date>-<check>/` (screenshots, `results.json`).
