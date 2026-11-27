# Testing

How ZenLead is tested, and where things go. The plan is [plans/testing-plan.md](../../plans/testing-plan.md); each workstream (T00–T10) has its own doc in [plans/testing-features/](../../plans/testing-features/README.md).

## Run

| What | Command | Needs |
|---|---|---|
| Backend unit + API tests | `dotnet test ZenLead.Tests` | nothing (SQLite in memory) |
| Angular unit tests | `cd ZenLead.Client; npm test` | Node |
| Azurite blob test | `$env:AZURITE='1'; dotnet test ZenLead.Tests --filter Category=Azurite` | Azurite running |
| Live AI check | `$env:RUN_LIVE_AI='1'; dotnet test ZenLead.Tests --filter Category=Live` | a real `OpenAI:ApiKey` (costs money) |
| Integration (T03) | `dotnet test --filter Category=Integration` | LocalDB, Azurite (arrives with T03) |
| E2E / browser (T07, T08) | the API in the `E2E` environment (see the root README) plus the Playwright suite | LocalDB, Azurite (arrives with T07) |
| Packs (T09) | `tools/test-runner/run.ps1 -Pack smoke\|regression\|release` | arrives with T09 |

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
