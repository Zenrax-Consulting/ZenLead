# T03 — Integration Testing

**Branch:** `test/integration` · **Size:** L · **Depends on:** T00 · **Provides:** `SqlServerFixture`, `HangfireTestServer`, `ProviderStubs` for T05, T06 and T10

## Goal
Test our code against the **real** infrastructure it runs on: SQL Server (not SQLite), Hangfire on SQL Server storage, Azurite blob, and HTTP-level stubs of SendGrid, OpenAI and PDL. This catches the class of bugs that the SQLite `TestDb` can't: unique-index and collation behaviour, `LIKE` escaping, query-filter translation, `ExecuteUpdate`/bulk paths, migrations, transaction isolation, and SDK serialisation.

## Project
New `ZenLead.IntegrationTests/` (xUnit, references the same projects as `ZenLead.Tests`, plus `ZenLead.Tests` for builders and fakes). Every class is `[Trait("Category","Integration")]`. Kept separate so `dotnet test ZenLead.Tests` stays fast and offline.

Packages: `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.MsSql` (optional path, D1), `WireMock.Net`, `Respawn` (fast DB reset between tests), `Azure.Storage.Blobs`.

## Fixtures (`ZenLead.IntegrationTests/Support/`)
| Fixture | Behaviour |
|---|---|
| `SqlServerFixture` | `ZENLEAD_TEST_SQL=localdb` (default): creates `ZenLeadIT_{guid}` on `(localdb)\mssqllocaldb`, applies **migrations** (`Database.MigrateAsync`, not `EnsureCreated`, so migrations are tested too), drops it on dispose. `=container`: same against an `MsSqlContainer`. One DB per test collection, Respawn reset between tests (keeps `__EFMigrationsHistory` and the Hangfire schema). |
| `IntegrationApiFactory` | `WebApplicationFactory<Program>` like `ApiFactory`, but over `SqlServerFixture`, with Hangfire **enabled** on the same DB, the recurring sender job disabled, and providers pointed at WireMock base URLs. |
| `HangfireTestServer` | Starts a `BackgroundJobServer` with 1 worker against the fixture DB, so tests can enqueue and wait for a job to finish (poll `IMonitoringApi` with a timeout). Used heavily by T05. |
| `AzuriteFixture` | Skips (xUnit `SkippableFact`) with a clear message if Azurite isn't reachable; creates a container per test class. Moves the existing `Category=Azurite` test here. |
| `ProviderStubs` | WireMock servers: **SendGrid** (`POST /v3/mail/send` → 202 with `X-Message-Id`; configurable 429/500/timeout), **OpenAI** (chat completions returning canned compose/classify JSON with `usage`), **PDL** (person search pages, 402/429, credit headers). Every request is recorded so tests assert on the outbound payload. |

## Test cases

### Persistence on SQL Server (F11, F13, F19–F28)
- **Migrations:** apply from empty → latest succeeds; `dotnet ef migrations has-pending-model-changes` equivalent (`context.Database.HasPendingModelChanges()` false); upgrade from a **Phase 1 snapshot DB** (script `Fixtures/Sql/phase1-baseline.sql` generated once from the `AddAiUsageLog` migration with sample rows incl. a mixed-case duplicate email pair) → the F11 migration fails loudly; after the documented fix it succeeds (parking-lot F11 item).
- **Unique indexes:** `(WorkspaceId, Email)` on Lead is case-insensitive under the DB collation, and the same email is allowed in two workspaces; `(CampaignId, LeadId)` enrollment; `(EnrollmentId, StepId)` on EmailMessage (F23 double-send guard); `sg_event_id` on ProcessedWebhookEvent; Message-ID dedupe on inbound.
- **Query filters:** for each `ITenantEntity` (reflection over the model), a row in workspace A is invisible under `ICurrentWorkspace=B` and under `null`; `ForWorkspace(id)` returns exactly A's rows and still honours soft delete. Also runs against SQLite in T10 to prove both providers agree.
- **Search:** `LeadRepository` `q` with `%`, `_`, `[` matches literally on SQL Server (parking-lot F13 `q=%`); sorting by every key is stable with ties (secondary key on Id).
- **Concurrency:** two contexts enrolling the same lead → one wins, the other gets the mapped 409, not a 500; two sender runs claiming the same due enrollment → exactly one `EmailMessage` (backs up `DisableConcurrentExecution`).
- **Aggregates (F28):** analytics queries return the same numbers as an in-memory LINQ recompute over the `analytics` seed; a DST-crossing range is zero-filled.

### API on the real stack
- `IntegrationApiFactory` re-runs a **tagged subset** of the T02 contract tests (`[Trait("Stack","Both")]`) against SQL Server, so the same assertions pass on both providers. That's cheaper than duplicating them.

### External providers through stubs
- **SendGrid sender (F18/F23):** payload has `from`, `reply_to` (inbound token address), `custom_args.emailMessageId/workspaceId`, `headers.List-Unsubscribe` + `List-Unsubscribe-Post`, plain + HTML parts; 429 → mapped transient result; 5xx after the request was sent → treated as **uncertain** → at-most-once (no resend) per deviation #12.
- **OpenAI (P1 compose, F23 personalisation, F26 classify):** the real Semantic Kernel client against WireMock. `usage` is parsed into `AiUsageLog` with the right purpose/campaign/enrollment; a timeout retries once; malformed JSON → `InvalidResponse` with billed tokens still logged; budget cap checked **before** the HTTP call (WireMock records zero requests when over the cap).
- **PDL (F14):** real `PdlLeadSource` against WireMock pages; cursor paging; 402/429 → backoff then `Failed` with a reason; results with no work email dropped; credits used = records returned.
- **Azurite (F15):** upload → `OpenReadAsync` stream is readable end to end through the CSV reader (parking-lot F15 Azurite item); a 10 MB file streams without loading fully (memory assertion, loose).

## Run
```
dotnet test ZenLead.IntegrationTests                         # LocalDB
$env:ZENLEAD_TEST_SQL='container'; dotnet test ZenLead.IntegrationTests   # Docker SQL Server
```
Target duration under 5 minutes on a dev laptop. Anything slower moves to `Category=Performance` (T06).
