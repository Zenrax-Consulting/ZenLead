# T10 — Parity Testing

**Branch:** `test/parity` · **Size:** M · **Depends on:** T01 (published-local mode), T03 (SQL Server fixture, provider stubs) · **Hands to:** F30.6 "first production deploy & parity check"

## Goal
ZenLead runs in several pairs of "same thing, different form": SQLite vs SQL Server in tests, `ng serve` vs the published single artifact, fakes vs real providers, LocalDB/Azurite/user-secrets vs Azure SQL/Blob/Key Vault, API vs UI, plan vs build. Each pair can drift silently. This workstream proves each pair behaves the same, or lists the differences on purpose.

## 1. Parity pairs

| # | Pair | Risk if they drift | How it's checked | Automated? |
|---|---|---|---|---|
| PA1 | **SQLite `TestDb`/`ApiFactory` ↔ SQL Server** | Unit tests pass while prod fails: case-sensitivity of unique email, `LIKE` escaping, query-filter translation, `DateTimeOffset` ordering, decimal precision on AI cost, cascade behaviour | `ProviderParityTests`: the same test bodies (abstract base class) run with a `SqliteContext` and a `SqlServerContext` subclass: tenant filter per entity, unique indexes, search escaping, soft delete, AI cost sum, analytics aggregates. Known differences are listed in `ProviderDifferences.md` with the test that pins each one. | ✅ Integration |
| PA2 | **`ng serve` + proxy ↔ published single artifact** (deviation #9) | Routes 404 only in prod, SPA fallback swallowing `/api` 404s, `/hangfire` and `/unsubscribe` behaving differently, missing `wwwroot` assets, base href | `tools/parity/dev-vs-published.ps1`: runs the T07 `@smoke` + T02 `spa-fallback` specs against both targets (`E2E_TARGET=dev` / `published`) and diffs the result JSON. Also checks `ShouldRunBuildScript` and that the publish output contains `wwwroot/index.html` + hashed bundles. | ✅ script |
| PA3 | **Development ↔ Production config paths** | Prod-only startup failures (`StartupConfiguration`, `Log` provider rejected, Sending mode required, missing storage config falling back to Azurite (parking-lot F15)) | `StartupParityTests`: boot `WebApplicationFactory` with `Production` and a complete fake config → starts; remove each required key in turn → fails with the named message; `Email:Provider=Log`/`Pickup` → fails; no `Storage:*` in Production → **expected to fail** (open finding until F30 decides) | ✅ Unit |
| PA4 | **Fake ↔ real providers (contract parity)** | Fakes accept what the real provider rejects (or the reverse), so tests prove nothing | For each interface, one **contract test suite** (abstract) run against the fake *and* the real implementation over WireMock recordings of real responses: `IEmailSender` (`FakeEmailSender`, `Log`, `Pickup`, `SendGrid`), `ILeadSource` (`FakeLeadSource`, `PdlLeadSource`), `IEmailComposer` / `IReplyClassifier` (fake vs Semantic Kernel), `IBlobStorage` (fake vs Azurite). Recordings refreshed by a `Live` script (`tools/parity/record-providers.ps1`, one real call each, PII scrubbed). | ✅ Integration + `Live` refresh |
| PA5 | **Parsed fields ↔ raw MIME inbound** (`Inbound:ParseMode`) | Switching the mode in prod changes threading or quarantine results | The same F25 fixtures (where both forms were captured) through both parsers → same `InboundEmail` model (from, to, subject, Message-ID, References, cleaned text) | ✅ Unit |
| PA6 | **API ↔ UI contract** | Angular `*.models.ts` drift from server DTOs (renamed or nullable fields), UI calling endpoints that don't exist, or endpoints no UI uses | `tools/parity/api-ui-contract.ts`: reads the OpenAPI snapshot (T02 §3) and the client's `*.service.ts` / `*.models.ts` via the TypeScript compiler API → reports (a) UI calls with no matching route/method, (b) model fields missing in or typed differently from the schema, (c) routes with no UI caller (expected for webhooks, ops, test, so allow-listed). Fails on (a) and (b). | ✅ script (Node) |
| PA7 | **Fresh DB ↔ upgraded DB** | Prod (migrated step by step from day 1) differs from a fresh `MigrateAsync` | Apply all migrations to an empty DB and to the Phase 1 baseline (T03) → compare schemas (`INFORMATION_SCHEMA` + indexes + filters dump, diffed) → identical. Plus `HasPendingModelChanges() == false`. Also produce `dotnet ef migrations bundle` and run it against an empty LocalDB (the F30 deploy path). | ✅ Integration |
| PA8 | **Plan ↔ build (feature parity)** | A PBI marked done but missing, or a deviation not recorded | `docs/testing/parity-checklist.md` § Plan: one row per PBI in F11–F28 → built? (link to code/test) → deviation? (README table row) → covered by test? (T09 traceability). Filled once now, updated per feature. | Manual, with the T09 report |
| PA9 | **Restricted ↔ Live sending** | A test or UAT environment ends up `Live` | `SendingModeParityTests`: in each env config (`appsettings.*.json`, E2E env file, integration factory) `Live` never appears; startup logs the mode loudly (exists); `Live` requires an explicit setting in Production (F30 check) | ✅ Unit |
| PA10 ⏳ F30 | **Local ↔ Azure** (LocalDB ↔ Azure SQL, Azurite ↔ Azure Blob + managed identity, user-secrets ↔ Key Vault, file/console logs ↔ App Insights, tunnel ↔ prod webhook URLs, Hangfire on Azure SQL incl. auto-pause) | First prod run exposes integration bugs days before Gate 2 (parent risk table) | `docs/testing/parity-checklist.md` § Azure, handed to F30.6 as its script: per row the local check that passed and the Azure check to run (migrations bundle from scratch, sender job survives restart, CSV via Blob, real send + reply via prod URLs, `/health`, Key Vault-only secrets, App Insights receiving traces with no PII, Data Protection key ring persisted (parking-lot F14) so the Hangfire cookie survives restarts). After deploy: rerun T07 `@smoke` and the T02 black-box suite with `E2E_TARGET=https://leads.zenraxconsultancy.com` in a **read-mostly mode** (no test-support endpoints there; uses a dedicated UAT workspace). | Checklist + reused suites |

## 2. Files
```
ZenLead.IntegrationTests/Parity/
  ProviderParityTests.cs (+ SqliteVariant / SqlServerVariant)
  ProviderDifferences.md
  MigrationParityTests.cs
  Contracts/EmailSenderContract.cs, LeadSourceContract.cs, ComposerContract.cs, ClassifierContract.cs, BlobStorageContract.cs
  Recordings/  (WireMock mappings, scrubbed)
ZenLead.Tests/Parity/
  StartupParityTests.cs  SendingModeParityTests.cs  InboundParseModeParityTests.cs
tools/parity/
  dev-vs-published.ps1  api-ui-contract.ts  record-providers.ps1 (Live)  schema-dump.sql
docs/testing/parity-checklist.md   (§ Plan, § Local pairs, § Azure ⏳ F30)
```

## 3. Exit
All automated pairs green. PA8 checklist filled for F11–F28. PA10 rows written with the local half ticked, ready for F30.6. Every intentional difference is documented next to the test that pins it.
