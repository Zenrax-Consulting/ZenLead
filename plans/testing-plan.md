# ZenLead — Test Plan (Phase 1 + Phase 2 Features 11–28)

**Scope:** everything built in Phase 1 (Features 1–10) and Phase 2 up to and including **Feature 28** (Event Webhook & Analytics). Features 29 (hardening), 30 (Azure + CI/CD) and 31 (UAT) are not built yet. This plan says what each of them will rely on from this testing work, and which checks have to wait for them (marked **⏳ F30**).

**Status:** plan only, nothing implemented. Written 2026-11-26 against `master` at `9450463` (F23 merged). **Revisited 2026-11-27 against `f5ad849`: F11–F28 are all on `master`**, so nothing in this plan waits on a product feature any more (only the ⏳ F30 items). Decisions D1–D6 are confirmed, and T00's own decisions are closed in its doc.

**How to read this:** this file is the overview: goals, tools, environments, decisions, the feature × test-type matrix and the parallel schedule. Each workstream has its own file-by-file plan in [testing-features/](testing-features/README.md). Each workstream is one branch and one PR, like the product features.

---

## 1. Goals

1. **Gate 2 confidence.** Every Gate 2 criterion (parent plan §7) has an automated or scripted check: the full loop runs (super admin → workspace → register/approve → leads → campaign → send → reply → inbox → analytics), cross-workspace access is denied, non-public endpoints need auth, and unapproved users can't sign in.
2. **Never email a real prospect from a test.** Every test environment runs with fake senders, or with `Sending:Mode=Restricted` and an allow-list of tester mailboxes. Tests check this rule too, not just follow it (see [T05](testing-features/T05-background-jobs-and-email.md)).
3. **No real OpenAI, SendGrid or PDL calls in automated suites** (CLAUDE.md rule). Real-provider checks are manual, scripted, rare and listed by name.
4. **Turn the parking lot into tests.** Most "Unconfirmed manual checks" and "Not verified" items in `docs/parking-lot.md` become automated tests or scripted checks in one of the workstreams below. Each item is traced in [T09](testing-features/T09-regression.md).
5. **Something F30 can wire into CI unchanged.** Every suite runs from one command with no interaction, and exits non-zero on failure.

## 2. What exists today (baseline)

| Layer | Existing | Gaps this plan fills |
|---|---|---|
| xUnit (`ZenLead.Tests`, ~85 files) | Domain rules, use cases with fakes, `ApiFactory` (real pipeline over **SQLite in-memory**), `TestDb`, `AuthorizationMatrixTests`, `TenantIsolationTests`, `NullWorkspaceTests`, SendGrid payload mapping, Hangfire dashboard filter, one `Category=Azurite` test | No SQL Server-backed tests (unique indexes, query filters, `LIKE` escaping and migrations on the real provider). No job crash/resume tests. No test categories beyond Azurite. |
| Angular (Vitest, 30 specs) | Auth service/guard/interceptor, auth screens, leads, import wizard, profiles | No template-render tests on complex screens (stepper, profiles list). Campaigns, inbox and analytics specs arrive with F24, F27 and F28. |
| Browser | `tools/gate1-rehearsal` (one Playwright script, Phase 1 flows, `playwright-core`, Chrome only) | No Playwright Test project, no Page Objects, no Phase 2 flows, no cross-browser runs, no accessibility checks |
| Load / security / parity | None | All |
| CI | None (arrives in F30) | Suites must run from scripts now and from GitHub Actions later |

## 3. Test types and where each lives

| Test type | Main tool | Location | Runs against | Workstream |
|---|---|---|---|---|
| Local testing | PowerShell scripts + checklists | `tools/local-env/`, `docs/testing/local-smoke-checklist.md` | Dev machine: LocalDB, Azurite, fakes | [T01](testing-features/T01-local-testing.md) |
| API testing | xUnit + `ApiFactory`; Playwright `request` fixture (TS) for black-box tests | `ZenLead.Tests/Api/**`, `ZenLead.E2E/tests/api/**` | In-process API; running API | [T02](testing-features/T02-api-testing.md) |
| Integration testing | xUnit + SQL Server (LocalDB, or Testcontainers on CI), Azurite, WireMock.Net | `ZenLead.IntegrationTests/` (new) | Real SQL Server, Azurite, stubbed HTTP providers | [T03](testing-features/T03-integration-testing.md) |
| Security testing | xUnit (authz/tenancy/token), Playwright (browser security), OWASP ZAP baseline, `dotnet list package --vulnerable`, `npm audit`, gitleaks | `ZenLead.Tests/Security/**`, `ZenLead.E2E/tests/security/**`, `tools/security/` | In-process API, running app | [T04](testing-features/T04-security-testing.md) |
| Background job & email testing | xUnit job harness (real Hangfire on SQL Server), email pickup sink, SendGrid fixtures, scripted real-mailbox run | `ZenLead.IntegrationTests/Jobs/**`, `ZenLead.Tests/Fixtures/**`, `docs/testing/email-live-checklist.md` | SQL Server + Hangfire, fakes, Restricted SendGrid | [T05](testing-features/T05-background-jobs-and-email.md) |
| Performance & load | k6 (TypeScript scripts), xUnit perf benchmarks, seeding tool | `tools/load/`, `ZenLead.IntegrationTests/Performance/**` | **Published** build on LocalDB/SQL Server | [T06](testing-features/T06-performance-and-load.md) |
| End-to-end | **Playwright Test + TypeScript**, Page Object Model | `ZenLead.E2E/` (new) | Running API + SPA in the `E2E` environment | [T07](testing-features/T07-e2e-playwright.md) |
| API regression and suite composition | Tags (`@smoke`, `@regression`, `@critical`), xUnit traits, one runner script | `tools/test-runner/` | All of the above | [T09](testing-features/T09-regression.md) |
| Browser testing | Playwright projects (Chromium, Firefox, WebKit, Edge, mobile), `@axe-core/playwright`, screenshot comparison | `ZenLead.E2E/` (extra projects and specs) | Same as E2E | [T08](testing-features/T08-browser-testing.md) |
| Parity testing | xUnit (SQLite vs SQL Server, fake vs real contract), scripts (dev vs published), API↔UI contract check, plan↔build checklist | `ZenLead.IntegrationTests/Parity/**`, `tools/parity/`, `docs/testing/parity-checklist.md` | Several environment pairs | [T10](testing-features/T10-parity-testing.md) |

**Shared foundation:** [T00](testing-features/T00-test-foundation.md). It adds test categories, the `E2E` environment, the email pickup sink, the deterministic seed and the test-data builders. Everything else builds on it.

## 4. Environments

| Name | `ASPNETCORE_ENVIRONMENT` | DB | Blob | Email | AI / leads | Used by |
|---|---|---|---|---|---|---|
| Unit | (in-process) | SQLite in-memory (`TestDb`, `ApiFactory`) | Fake | `FakeEmailSender` | Fakes | xUnit, every PR |
| Integration | `Development` (in-process) | LocalDB, or SQL Server in a container, **new DB per test class** | Azurite | Fake, or WireMock-stubbed SendGrid | WireMock-stubbed OpenAI/PDL | T03, T05, T06, T10 |
| E2E | **`E2E`** (new, see D2) | LocalDB `ZenLeadE2E`, reset per run | Azurite | **Pickup** sink (files) | `Fake` lead source, fake composer/classifier | T07, T08, T04 browser part |
| Local dev | `Development` | LocalDB `ZenLead` | Azurite | `Log`, or SendGrid in **Restricted** mode | Real (owner's keys), used sparingly | T01, manual checks |
| Live-email rehearsal | `Development` | LocalDB | Azurite | **SendGrid, `Restricted`**, allow-list of tester mailboxes | Real OpenAI (capped) | T05 live checklist, T10 fake↔real parity |
| Published-local | `Production`-like (`dotnet publish` output run locally) | LocalDB | Azurite | `Log` blocked → SendGrid Restricted | Fakes where config allows | T06 load, T10 dev↔published parity |
| Azure prod ⏳ F30 | `Production` | Azure SQL | Azure Blob | SendGrid Restricted until Gate 2 | Real | T10 env parity, F31 UAT smoke |

## 5. Decisions (confirmed 2026-11-27)

| # | Decision | Recommendation | Why |
|---|---|---|---|
| D1 | SQL Server for integration tests | **LocalDB fixture by default** (already installed, no Docker). Use `Testcontainers.MsSql` when `ZENLEAD_TEST_SQL=container`, for the Linux CI runner in F30. | Devs are on Windows with LocalDB. A Linux CI runner has no LocalDB. |
| D2 | How E2E reads emails and controls time | New **`E2E` environment** that enables (a) `Email:Provider=Pickup`, which writes each email as JSON to a folder, and (b) a test-only `TestSupportController` (`/api/v1/test/*`: reset/seed, run recorded jobs, advance the clock). Hangfire is off in `E2E`; enqueued jobs are recorded and drained by the test (T00-1). It is **only mapped when the environment is `E2E`**, and a startup guard plus a test prove it is absent in every other environment. | Approval/reset links and campaign mail have to be readable by the browser test, and follow-up steps are days apart. The alternative is waiting for the minutely job and mocking time from outside, which is slow and flaky. |
| D3 | E2E project location and runner | New top-level **`ZenLead.E2E/`** (own `package.json`, `@playwright/test`, TypeScript, `playwright.config.ts`). It replaces `tools/gate1-rehearsal`, which is kept until the T07 smoke suite covers its flows, then deleted. | Keeps Playwright out of the Angular app's dependencies. One place for E2E, browser and black-box API specs. |
| D4 | Load tool | **k6 with TypeScript scripts** (k6 runs `.ts` directly). Install it with `winget install k6`. | Same language as the E2E suite. Built-in thresholds fail the run. Nothing to add to the solution. |
| D5 | Where the E2E and load runs point | The **published build** (`dotnet publish` then run the DLL, SPA served from `wwwroot`) for T06 and the T08 nightly run. `ng serve` with the proxy for the fast T07 dev loop. | The published build is what ships (one deployable). Parity between the two is checked in T10. |
| D6 | Real-provider runs | Manual, scripted, recorded in `docs/testing/evidence/`. At most one real PDL run and one live-email round trip per release candidate. | Cost, and the rule against emailing real people. |

## 6. Feature × test-type matrix

✅ = this workstream covers the feature; **P0** = must pass for Gate 2; ⏳ = waits on F30.

| Feature | API (T02) | Integ. (T03) | Security (T04) | Jobs/Email (T05) | Perf (T06) | E2E (T07) | Browser (T08) | Parity (T10) |
|---|---|---|---|---|---|---|---|---|
| P1 Auth, JWT, refresh rotation | ✅ | ✅ | **P0** token tamper, rotation reuse | – | login burst | **P0** login/logout/refresh | ✅ | – |
| P1 Compose email (AI) | ✅ | WireMock OpenAI | prompt injection, budget | – | – | ✅ fake composer | – | fake↔real composer |
| F11 Company, ingestion, tenancy | ✅ | **P0** unique index + query filter on SQL Server | **P0** cross-workspace | – | ingestion batch | ✅ | – | SQLite↔SQL Server |
| F12 Shell | – | – | – | – | – | ✅ nav, deep link | **P0** responsive | dev↔published routing |
| F13 Leads list/detail | **P0** paging/sort/filter | `LIKE` escaping | `q=%` injection | – | **P0** 50k list p95 | ✅ | ✅ | – |
| F14 Discovery | ✅ | WireMock PDL, cap | rate limit, cross-ws | **P0** run job resume/idempotent, cap | run 500 leads | ✅ fake source | – | Fake↔PDL contract |
| F15 CSV backend | ✅ | Azurite blob | upload validation, CSV formula injection | **P0** job resume, re-run imports 0 | 50k rows | – | – | Azurite↔Blob ⏳ |
| F16 Import wizard | – | – | – | – | – | **P0** upload→result | drag-drop per browser | – |
| F17 SendGrid DNS | – | – | SPF/DKIM/DMARC check | live deliverability | – | – | – | – |
| F18 Email sender | ✅ | WireMock SendGrid | – | ✅ payload mapping | – | – | – | Log↔SendGrid payload |
| F19 Super admin | **P0** matrix | seeder on SQL Server | **P0** 403 both ways, audit | – | – | ✅ | – | – |
| F20 Membership/approval | ✅ | ✅ | **P0** token single-use, enumeration, caps | ✅ approval email | – | **P0** register→approve→login | – | – |
| F21 Password reset | ✅ | ✅ | **P0** timing, replay, sessions revoked | ✅ reset/notice email | – | **P0** forgot→reset | – | – |
| F22 Campaign domain/API | **P0** | unique enrollment | cross-ws | – | – | ✅ | – | – |
| F23 Sending engine | ✅ | **P0** `(EnrollmentId, StepId)` unique | unsubscribe token, Restricted mode | **P0** idempotency, crash, caps, DST, suppression | **P0** 1k due enrollments | ✅ step 1 via pickup | – | Restricted mode on every env |
| F24 Campaigns UI | activation-preview, sender | – | – | – | – | **P0** build→enroll→activate | ✅ | API↔UI |
| F25 Inbound webhook | ✅ fixtures | quarantine on SQL Server | **P0** secret, sanitizer, size | **P0** threading levels, dedupe | webhook burst | ✅ simulated reply | – | parsed↔raw MIME |
| F26 Classification | – | WireMock OpenAI | **P0** prompt injection fixtures | ✅ job, OOO, cap | – | ✅ fake classifier | – | fake↔real classifier |
| F27 Inbox | **P0** | indexes | XSS rendering, cross-ws | ✅ reply threading headers | thread list | **P0** read/reply/reclassify | ✅ polling | – |
| F28 Events & analytics | **P0** | aggregates on SQL Server | **P0** ECDSA, replay | ✅ event idempotency | 500-event batch, summary | ✅ dashboard | ✅ SVG charts | – |

## 7. Parallel workstreams and schedule

```
Lane A (backend)  T00 ──► T03 Integration ──► T05 Jobs & email ──► T06 Perf/load
Lane B (QA/TS)    T00 ──► T07 E2E Playwright ──► T08 Browser
Lane C (security) T00 ──► T02 API testing ──► T04 Security
Lane D (devops)   T01 Local testing (day 1, no T00 dependency) ──► T10 Parity ──► T09 Regression packs + runner
```

- **T00 is the only blocker.** It is small to medium (about a day). Lanes start as soon as it merges. T01 doesn't need it and starts immediately.
- T02 and T04 overlap on the authorization matrix: **T02 owns the endpoint inventory, T04 owns the assertions on it.** Merge T02 first, or agree the inventory file's shape on day 1.
- T05 needs the integration fixture from T03 (`SqlServerFixture`, `HangfireTestServer`). Start T05's fixture-only tests (inbound and event payloads) in parallel with T03.
- T07 covers every flow from the start: F24, F27 and F28 are on `master`, so the campaigns UI, inbox and analytics specs are written against the real screens.
- T09 comes last because it composes the others' tags. Its tagging rules are fixed in T00 so nobody has to retag later.

| Workstream | Branch | Size | Depends on | Waits on |
|---|---|---|---|---|
| [T00 Test foundation](testing-features/T00-test-foundation.md) | `test/foundation` | S–M | – | – |
| [T01 Local testing](testing-features/T01-local-testing.md) | `test/local-env` | S | – | – |
| [T02 API testing](testing-features/T02-api-testing.md) | `test/api` | M | T00 | – |
| [T03 Integration testing](testing-features/T03-integration-testing.md) | `test/integration` | L | T00 | – |
| [T04 Security testing](testing-features/T04-security-testing.md) | `test/security` | M | T00, T02 inventory | – |
| [T05 Background jobs & email](testing-features/T05-background-jobs-and-email.md) | `test/jobs-email` | L | T00, T03 fixture | – |
| [T06 Performance & load](testing-features/T06-performance-and-load.md) | `test/perf-load` | M | T00, T03 fixture (adds its own `bulk:{n}` seed) | – |
| [T07 E2E (Playwright + TS)](testing-features/T07-e2e-playwright.md) | `test/e2e-playwright` | L | T00 | – |
| [T08 Browser testing](testing-features/T08-browser-testing.md) | `test/browser-matrix` | M | T07 skeleton | – |
| [T09 Regression](testing-features/T09-regression.md) | `test/regression-packs` | S | all tags | – |
| [T10 Parity testing](testing-features/T10-parity-testing.md) | `test/parity` | M | T01, T03 | Azure parts ⏳ F30 |

## 8. Exit criteria (test sign-off for "Features 1–28 done")

- [ ] `tools/test-runner/run.ps1 -Pack regression` is green on a clean machine: xUnit unit + integration, Vitest, Playwright `@regression` on Chromium, k6 smoke thresholds.
- [ ] All **P0** cells in §6 have a passing test, linked in the T09 traceability table.
- [ ] Security: zero high or critical findings open from ZAP, `dotnet list package --vulnerable` and `npm audit`. Gitleaks finds nothing across the full history.
- [ ] Performance budgets in T06 §3 met on the published build.
- [ ] Browser matrix (T08) green on Chromium, Firefox, WebKit and Edge for `@smoke`. No serious or critical axe violations on the core screens.
- [ ] One recorded live-email round trip (T05 §6) in Restricted mode: send → receive → reply → classify → event → analytics.
- [ ] Parity checklist (T10) complete for the local pairs. Azure rows handed to F30.6 as its checklist.
- [ ] Parking-lot items either covered by a test or explicitly re-parked with a reason (T09 §5).

## 9. Out of scope for this plan

Writing the tests themselves (this is the plan). Real-traffic load against Azure (⏳ F30, and only at the B1 single-org scale). Penetration testing by a third party. Mobile apps. MFA/SSO (not built). Chaos testing of Azure services. Testing Features 29–31 beyond what is listed as handed to them.
