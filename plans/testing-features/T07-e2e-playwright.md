# T07 — End-to-End Testing (Playwright + TypeScript)

**Branch:** `test/e2e-playwright` · **Size:** L · **Depends on:** T00 (`E2E` env, pickup mail, test-support endpoints, seeds) · **Waits on:** F24, F27, F28 for the campaign-UI, inbox and analytics specs (write them against the feature docs and enable them when merged)

## Goal
Automate the real user journeys through the real Angular UI and API, from the super admin creating the workspace to reading analytics, so the Gate 2 loop is a green test rather than a manual rehearsal. Replace `tools/gate1-rehearsal` (D3).

## 1. Project layout (`ZenLead.E2E/`)
```
ZenLead.E2E/
  package.json              # @playwright/test, typescript, @axe-core/playwright (T08), dotenv
  tsconfig.json             # strict, paths: @pages/*, @fixtures/*, @support/*
  playwright.config.ts
  global-setup.ts           # boots / checks the E2E host, resets the DB
  global-teardown.ts
  .env.e2e.example          # BASE_URL, API_URL, MAIL_DIR, generated keys (no real secrets)
  support/
    api-client.ts           # typed wrapper over APIRequestContext for /api/v1 (login, seed, jobs, clock)
    mailbox.ts              # reads pickup JSON: waitForEmail({to, subject}), extractLink(email, /approve\?token=/)
    sendgrid.ts             # builds inbound multipart + signed event batches (Node crypto P-256)
    data.ts                 # unique emails/names per test (run id + worker index)
    csv.ts                  # generates CSV files (clean, messy ;-delimited, Latin-1, duplicates)
  fixtures/
    test.ts                 # extends base test: api, mailbox, seed, loggedInAs(role), pages
  pages/                    # Page Object Model, one class per screen, selectors by role/label first
    login.page.ts  register.page.ts  approve.page.ts  forgot-password.page.ts  reset-password.page.ts
    shell.page.ts  leads-list.page.ts  lead-detail.page.ts  import-wizard.page.ts
    target-profiles.page.ts  run-discovery.dialog.ts  admin-workspaces.page.ts  change-password.page.ts
    campaigns-list.page.ts  campaign-builder.page.ts  enrollment.page.ts
    inbox.page.ts  thread.page.ts  analytics.page.ts  unsubscribe.page.ts
  tests/
    smoke/  auth/  admin/  leads/  discovery/  import/  campaigns/  inbox/  analytics/  public/  journeys/
    api/        # T02 black-box specs
    security/   # T04 browser specs
    browser/    # T08 a11y / visual / responsive
```

## 2. Configuration (`playwright.config.ts`)
- `webServer`: optional. With `E2E_START=1`, starts the API (`dotnet run --project ../ZenLead.Api --no-launch-profile`, `ASPNETCORE_ENVIRONMENT=E2E`, `ASPNETCORE_URLS=http://localhost:5199`) and `ng serve --port 4300 --proxy-config src/proxy.conf.js` (same as the Gate 1 rehearsal), waiting on `/api/v1/health` (or the login page) and `http://localhost:4300`. With `E2E_TARGET=published`, a single server on the published build (T08 nightly).
- `use`: `baseURL`, `trace: 'retain-on-failure'`, `screenshot: 'only-on-failure'`, `video: 'retain-on-failure'`, `testIdAttribute: 'data-testid'`.
- `projects`: `setup` (creates storage states), `chromium` (default dev loop). T08 adds Firefox, WebKit, Edge and mobile.
- `workers`: 2 locally (one LocalDB). `fullyParallel: true` *within* a file only when tests use isolated workspaces (default). `retries: 1` on CI, 0 locally. `reporter`: `list` + `html` + `junit` (for F30 CI).
- **Isolation strategy:** global setup resets the DB once. Each test creates its **own workspace and users** through the API (fast, no UI) via the `seed` fixture, so tests never share state and can run in parallel. Only `journeys/` tests drive the full flow through the UI.

## 3. Fixtures
| Fixture | Provides |
|---|---|
| `api` | `ApiClient` logged in as needed; `api.test.seed('gate2')`, `api.test.runJob('campaign-sender')`, `api.test.advanceClock(minutes)` |
| `mailbox` | `waitForEmail({ to, subjectContains, timeoutMs })` polling `MAIL_DIR`; `link(email, pattern)` |
| `workspace` | Fresh workspace + active admin user, created by the super admin via `/admin/workspaces` then registered+approved via API, returning creds |
| `loggedInAs(role)` | `'superAdmin' \| 'admin' \| 'member' \| 'pending'`. Logs in via the UI once per worker and reuses **in-memory** auth by replaying the login, because the access token is never persisted. Refresh-token-in-`localStorage` storage state makes reloads behave like real users. |
| `pages` | Lazily constructed page objects bound to `page` |

**Selector rule:** `getByRole`/`getByLabel`/`getByText` first; add `data-testid` in the Angular templates only where Material markup makes roles ambiguous (stepper steps, chip inputs, mat-select options, table rows). Those additions go into this workstream's PR as small, reviewed template edits.

## 4. Spec catalogue (tags in titles; P0 = `@critical`)
### Smoke (`@smoke`, < 3 min total, every PR)
- App loads, login, leads list renders, logout. Super admin lands on `/admin/workspaces`. Signed-out deep link → `/login`.

### Auth & accounts (P1, F19–F21)
| Spec | Flow |
|---|---|
| `auth/login-logout.spec.ts` @critical | Valid login → shell. Wrong password → generic error. Logout clears in-memory token; Back doesn't show data. |
| `auth/session-refresh.spec.ts` | Reload on `/leads` stays signed in (silent refresh). Expire the access token (clock advance) → next API call refreshes transparently. Revoked refresh → back to login with a message (expired-session behaviour, F29.6). |
| `auth/register-approve.spec.ts` @critical | Register picking a workspace from the type-ahead → "waiting for approval" → login says awaiting approval → admin email in the mailbox → open `/approve?token` → Approve → user email "approved" → login works and **sees the same leads as the admin** (shared workspace). |
| `auth/register-reject.spec.ts` | Reject → login shows the generic "not approved". Reused token → clear error page. |
| `auth/forgot-reset.spec.ts` @critical | Forgot → neutral message → reset email → `/reset-password?token` → token removed from the address bar → new password → old sessions signed out (second browser context gets bounced) → login with the new password. Unknown email shows the same neutral message and no email is written. |
| `auth/change-password.spec.ts` | Account menu → wrong current password rejected → success → notice email. |
| `admin/workspaces.spec.ts` @critical | Super admin creates "Zenrax E2E" with an admin email → appears in the list and in the public register picker. Duplicate name error. Super admin sees no tenant nav; visiting `/leads` redirects to the admin area. |

### Leads (F11–F16)
| Spec | Flow |
|---|---|
| `leads/list.spec.ts` @critical | Server-side paging/sort/search/filters reflected in the URL. Copy URL to a new page → same view (parking-lot F13). Status chips. Empty state with "Import CSV" CTA. |
| `leads/crud.spec.ts` | Create, edit, invalid status transition shows an error, duplicate email error, delete (soft) disappears from the list. |
| `leads/detail.spec.ts` | Company, source with a link to the run/batch, verification status, "Generate draft" with the fake composer (P1 flow), AI budget banner when the cap is set low. |
| `leads/bulk-select.spec.ts` | Select across two pages → open a lead → Back → selection intact (parking-lot F13). Bulk "Create target profile". |
| `import/wizard.spec.ts` @critical | Upload a generated 2k-row messy `;` CSV (drag-and-drop and file picker) → mapping auto-guessed → remove the email mapping blocks Continue → Review → Start → progress → result counts equal the expected dupes/invalids → download the errors CSV (check content) → "View imported leads" filters by batch. Re-upload the same file → 0 imported. |
| `import/resume.spec.ts` | Leave mid-progress, come back via Back with `?batch=` → resumes (parking-lot F16). |
| `discovery/profiles.spec.ts` | Create a profile with criteria chips. Create from lead detail and from bulk-select (suggested, unsaved, edit then save). Edit, delete. |
| `discovery/run.spec.ts` @critical | Run discovery (fake source) with a max → `api.test.runJob('discovery/{id}')` → progress dialog shows Completed with found/imported/duplicates/suppressed → link opens leads filtered by `SourceRunId`. Second run → mostly duplicates. Low cap → "Cap reached" (parking-lot F14). |
| `discovery/hangfire-access.spec.ts` | Admin-email user opens "Job dashboard" (cookie flow, handles popup) → `/hangfire` loads. Other user → denied (parking-lot F14). |

### Campaigns (F22–F24), enabled when F24 merges
| Spec | Flow |
|---|---|
| `campaigns/build.spec.ts` @critical | Create campaign → add 3 steps with delays 0/3/7 → token insert helper → timeline shows "Day 0, Day 3, Day 7" → reorder and delete step → "Preview with lead" renders tokens and the fallback → "Generate with AI" fills subject/body (fake composer). |
| `campaigns/enroll.spec.ts` | Enroll from the leads bulk-select and via "all matching filter" → preview of skipped counts (unsubscribed, already enrolled, invalid email, `Unverified` warning) → confirm → enrollment table. |
| `campaigns/activate.spec.ts` @critical | Activate with problems → every validation error listed. Fix → activation preview shows estimated AI calls/cost → Activate → sender-domain indicator. `runJob('campaign-sender')` → enrollment shows step 1 sent → **pickup mailbox has the email** with an unsubscribe link and List-Unsubscribe header. Advance the clock 3 days + run job → step 2 sent. Pause → no further sends. |
| `campaigns/restricted-mode.spec.ts` @critical | Lead email outside the allow-list (`@realdomain.invalid`) → enrollment shows "restricted", nothing in the mailbox. Proves the UAT safety rule in the UI. |
| `public/unsubscribe.spec.ts` @critical | Open the link from the pickup email → landing page → nothing changes until Confirm (prefetch-safe) → Confirm → lead `Unsubscribed`, enrollment stopped; revisiting is idempotent. |

### Inbox (F25–F27), enabled when F27 merges
| Spec | Flow |
|---|---|
| `inbox/reply-arrives.spec.ts` @critical | After a step-1 send, `sendgrid.ts` posts an inbound multipart built from that email's Reply-To and Message-ID (with the secret) → `runJob('classify-pending')` (fake classifier) → within the poll interval the thread appears with an unread badge in the shell nav and an "Interested" chip, threaded to the right lead/campaign; lead status `Replied`; step 2 never sends. |
| `inbox/unsubscribe-reply.spec.ts` | Reply text containing `[[unsubscribe]]` → classification Unsubscribe → enrollment paused, lead `Unsubscribed`. |
| `inbox/ooo.spec.ts` | Auto-reply fixture → not classified as human, sequence resumes per rules. |
| `inbox/answer.spec.ts` @critical | Open thread (marks read, badge decrements) → reply from the composer → pickup email has `In-Reply-To`/`References` → outbound message shows in the conversation. |
| `inbox/reclassify.spec.ts` | Manual reclassify → chip changes, "needs review" cleared. Filters by classification/unread/campaign map to the URL. |
| `inbox/polling.spec.ts` | New inbound while the list is open → appears without reload and **the selected thread stays selected**. Hidden tab → no polling (`page.evaluate` visibility override). |

### Analytics (F28), enabled when F28 merges
| Spec | Flow |
|---|---|
| `analytics/dashboard.spec.ts` @critical | Seed `analytics` → workspace tiles (sent, delivered, opened ≈ with MPP tooltip, replied, bounced), trend line + table fallback, date range change, `from>to` validation. |
| `analytics/events-flow.spec.ts` | Send step 1 → post a signed event batch (delivered, open, bounce for another lead) → the campaign view updates; the bounced lead shows `Bounced` and is suppressed from the next step. |

### Journeys (`journeys/`, @critical @regression, run serially)
- **`gate2-loop.spec.ts`**: the F31.1 UAT script, fully in the UI except for the webhook simulation and job triggers: super admin creates the workspace → user registers → admin approves from the email → forgot/reset once → target profile + discovery **and** CSV import → campaign with an AI step → activate → email in the mailbox → simulated reply → classified thread → reply from the inbox → event batch → analytics show the numbers. Screenshots at every step into `test-results/gate2/`, reusable as UAT evidence.
- **`isolation.spec.ts`**: two workspaces in two browser contexts. B pastes A's lead/campaign/thread URLs → not-found page, no data flashes. Super admin pasting tenant URLs → admin area. (Gate 2 internal criterion.)
- **`gate1-regression.spec.ts`**: ports every step of `tools/gate1-rehearsal/rehearsal.mjs`, adapted to the F20 registration flow, so the rehearsal script can be deleted.

## 5. Stability rules
- No `waitForTimeout`. Wait on web-first assertions, `waitForResponse` for specific API calls, or `expect.poll` for mailbox/job state.
- Jobs are triggered via `/api/v1/test/jobs/*`, never by waiting for the minutely cron (the recurring job is off in E2E).
- Time-based behaviour uses `advanceClock`. Angular polling intervals can be shortened only through E2E-specific environment config if needed, never with test-only code paths in components.
- Each spec cleans up nothing: isolation by fresh workspace. Global reset happens per run.
- A test that fails, passes on retry and repeats that is quarantined with `test.fixme` plus an issue, never left flaking in the regression pack.

## 6. Run
```
cd ZenLead.E2E
npm ci; npx playwright install chromium firefox webkit msedge
$env:E2E_START=1; npx playwright test --project=chromium              # all
npx playwright test --grep @smoke                                      # smoke
npx playwright test tests/journeys/gate2-loop.spec.ts --headed         # watch the Gate 2 loop
npx playwright show-report
```
