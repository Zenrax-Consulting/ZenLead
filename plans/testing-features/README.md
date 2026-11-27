# Testing workstreams (T00–T10)

File-by-file plans for each testing workstream in [testing-plan.md](../testing-plan.md). Read that first for the environments, decisions D1–D6, the feature × test-type matrix and the lanes.

Same format as `phase-2-features/`. Each doc covers scope, files to add, test cases, and how to run and verify. Each one is a branch and PR. Merge order: T00 and T01 first, then T02–T08 in parallel lanes, then T09 last.

| # | Workstream | Branch | Lane |
|---|---|---|---|
| T00 | [Test foundation](T00-test-foundation.md) | `test/foundation` | all (blocker) |
| T01 | [Local testing](T01-local-testing.md) | `test/local-env` | D |
| T02 | [API testing](T02-api-testing.md) | `test/api` | C |
| T03 | [Integration testing](T03-integration-testing.md) | `test/integration` | A |
| T04 | [Security testing](T04-security-testing.md) | `test/security` | C |
| T05 | [Background jobs & email](T05-background-jobs-and-email.md) | `test/jobs-email` | A |
| T06 | [Performance & load](T06-performance-and-load.md) | `test/perf-load` | A |
| T07 | [E2E with Playwright + TypeScript](T07-e2e-playwright.md) | `test/e2e-playwright` | B |
| T08 | [Browser testing](T08-browser-testing.md) | `test/browser-matrix` | B |
| T09 | [Regression](T09-regression.md) | `test/regression-packs` | D |
| T10 | [Parity testing](T10-parity-testing.md) | `test/parity` | D |

## Conventions every workstream follows

- **Tags and traits (fixed in T00):** xUnit `[Trait("Category", X)]` with X one of `Unit` (default, untagged), `Integration`, `Azurite`, `Performance`, `Live`. Playwright tags in test titles: `@smoke`, `@regression`, `@critical`, `@a11y`, `@visual`, plus a feature tag `@F11`…`@F28` / `@P1`. A test can carry several tags.
- **No real providers** in anything that isn't tagged `Live`. `Live` tests and scripts never run in a default command.
- **Restricted sending everywhere:** any config a test uses that selects `Email:Provider=SendGrid` must also set `Sending:Mode=Restricted` with an allow-list of `*@example.test` or tester mailboxes. T05 adds a test that fails if a test config breaks this.
- **Unique test data:** emails are `<purpose>-<guid>@example.test` (the reserved `.test` TLD, never deliverable). Workspace names carry a run id. No fixed passwords in the repo (use `ApiFactory.NewPassword()`, or a password generated per run in TS).
- **Cross-workspace case:** every new tenant endpoint test file gets one test where workspace B tries workspace A's resource (README convention from F11, repeated here because new test files must follow it).
- **Evidence:** manual or scripted checks record their output under `docs/testing/evidence/<date>-<check>/` (screenshots, `results.json`), the same way `docs/gate1-evidence/` does.
