# T09 — Regression Testing

**Branch:** `test/regression-packs` · **Size:** S · **Depends on:** tags from T00, suites from T02–T08 (can start with whatever has merged and grow)

## Goal
One command answers "did we break anything?" at the right depth for the moment: a pre-commit check, a PR, a cross-feature change, nightly, or a release candidate. Every bug fix and parking-lot item is pinned by a test, so it stays fixed. No CI exists until F30, so the runner is a script F30 can call step by step.

## 1. Packs (`tools/test-runner/run.ps1 -Pack <name>`)
| Pack | Contents | Target time | When |
|---|---|---|---|
| `quick` | `dotnet test ZenLead.Tests` (unit only, excludes Integration/Azurite/Performance/Live) + `ng test --watch=false` | < 2 min | Pre-commit |
| `pr` | `quick` + `ZenLead.IntegrationTests` (LocalDB) + Playwright `@smoke` (Chromium) + T02 black-box API + k6 `smoke` | < 12 min | Every PR (F30 CI later) |
| `touchpoint -Feature Fnn` | `pr` + the suites of every **earlier** feature that Fnn edits, from the phase-2 README "Cross-feature touchpoints" table (e.g. F23 → F11 ingestion, F22 enrollment, P1 compose, `AiUsageLog` tests; F25 → F23 sender; F26 → F25, F22 rules, composer; F28 → F23, F25) | < 15 min | PR that touches another feature's files |
| `regression` | Everything automated except `Live`/soak: unit, integration, Azurite, Vitest, Playwright `@regression` on Chromium, security scanners `deps` + `secrets`, k6 `smoke` | < 30 min | Nightly + before each merge to `master` on release days |
| `browser` | T08 matrix (`@smoke`+`@critical` on Chrome, Edge, Firefox, WebKit; `@responsive`; `@a11y`; `@visual`) | < 25 min | Nightly |
| `release` | `regression` + `browser` + `Performance` category + k6 `browse` + `webhook-burst` + ZAP baseline + T10 parity scripts + the manual checklists (local smoke, live email) marked done with evidence | ~2 h + manual | Release candidate, before Gate 2 |

The runner prints a summary table (pack step, pass/fail, duration, report path), writes `test-results/summary.json`, and exits non-zero if any step fails. It continues past failures so a single run shows everything. `-Only <step>` and `-Skip <step>` allow quick reruns.

## 2. Tagging rules (fixed in T00, enforced here)
- xUnit: untagged = Unit. `Category` traits as in the README conventions. Feature traits `[Trait("Feature","F23")]` on new tests (existing tests are mapped by namespace in `feature-map.json` rather than retagged).
- Playwright: every test has exactly one of `@smoke | @regression` (smoke tests are also in regression implicitly). `@critical` marks the P0 cells of the matrix. Feature tags `@F11`…`@F28`, `@P1`.
- `TagLintTests` (xUnit) and `tag-lint.ts` (Playwright `--list` + check) fail if a test lacks the required tags. That keeps the packs honest without anyone remembering to tag.

## 3. Traceability (`docs/testing/traceability.md`, generated)
`tools/test-runner/traceability.ps1` builds a table: **Feature/PBI → test files/names → last result**, from the feature traits, Playwright tags, `feature-map.json` and the latest `summary.json`. It highlights:
- PBIs with zero tests. Each P0 cell in the matrix (testing-plan §6) must map to at least one passing test.
- Gate 2 criteria (§7 of the phase-2 plan) and their covering tests.

## 4. Bug → test rule
Every bug found in testing, UAT or use gets a failing test **before** the fix. The test is named `Regression_<short-issue>` (xUnit) or titled `regression: <issue> @regression` (Playwright). `docs/testing/regression-log.md` lists the bug, the test and the fix commit. F31 UAT bugs (`docs/uat-results.md`) follow the same rule.

## 5. Parking-lot conversion (one-off, part of this PR)
Go through `docs/parking-lot.md` item by item and mark each one:
| Parking-lot item | Becomes |
|---|---|
| F11 two-workspace check via `.http` | T04 `CrossWorkspaceTests` + T07 `isolation.spec.ts` |
| F11 migration with a mixed-case duplicate | T03 migration upgrade test |
| F11 draft includes company | T03 OpenAI WireMock payload assertion |
| F12 shell checks (nav, workspace name, deep link, hamburger, refresh) | T07 smoke + T08 responsive |
| F13 50k list under 1 s, page 2000, sorts, `q=%` | T06 budgets + T03 `LIKE` escaping |
| F13 URL shareable, cross-page selection | T07 `leads/list`, `leads/bulk-select` |
| F14 UI pass, run twice, cap, kill mid-run, Hangfire cookie | T07 discovery specs + T05 crash/resume |
| F14 real PDL run | T10 fake↔real parity, `Live`, manual |
| F15 messy CSV end to end, kill mid-import, Azurite stream | T07 `import/wizard` + T05 crash/resume + T03 Azurite |
| F16 manual verification, stepper look on narrow screens | T07 `import/*` + T08 visual/responsive |
| Known gaps (undercount, stale `Running`, invalid rows in no counter, 409 not jumping to progress) | Pinning tests in T05/T07 that document current behaviour; product decision stays in the parking lot |

Items that stay manual (owner terms review, PDL tier details, F30 config) are left in the parking lot with "manual, not testable" noted.

## 6. Flake policy
A test is flaky if it fails then passes on retry twice within 7 nightly runs (from `summary.json` history). Flaky tests move to `@quarantine` (excluded from packs, still run nightly in a separate report) with an owner and a 5-working-day fix window. The quarantine list is shown at the top of every runner summary.
