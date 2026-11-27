# T06 — Performance & Load Testing

**Branch:** `test/perf-load` · **Size:** M · **Depends on:** T00 (seed content, builders), T03 fixture · **Owns:** the `bulk:{n}` seed (moved from T00, decision T00-3) · **Feeds:** F29.5 performance sanity

## Goal
Show that the single-org MVP meets clear latency and throughput budgets at the data sizes the plans name (50k leads, 10k–50k-row CSV, 1k due enrollments, webhook bursts). Find N+1 queries and missing indexes before Azure B1 / free-tier SQL (⏳ F30) makes them expensive. This is **sanity at the real scale**, not web-scale stress testing: the load model is three users plus webhooks.

## 1. Load model (from the plans)
| Actor | Volume |
|---|---|
| Interactive users | 3 concurrent (owner + 2 colleagues), plus headroom ×5 → **15 VUs** peak |
| Leads per workspace | 50k (F13/F29.5 target) |
| CSV upload | 10 MB / 50k rows max (F15 limit) |
| Sender job | 1k due enrollments per run (F29.5) |
| SendGrid event webhook | Batches up to ~500 events; bursts after a campaign send |
| Inbound replies | Low (tens/hour), but webhook latency must stay < 2 s for a fast 200 |

## 2. Environment
- **Published build** (`tools/local-env/start-app.ps1 -Published`) on LocalDB `ZenLeadPerf`, Hangfire enabled, fakes for AI/leads, `Email:Provider` = the WireMock SendGrid stub (T03) run as a standalone process (`tools/load/stubs/start-stubs.ps1`).
- Seed with `seed.ps1 -Scenario bulk:50000`, then a campaign with 1k active enrollments due now. T06 adds `bulk:{n}` as a standalone seeding tool that writes straight to the perf DB (`SqlBulkCopy`), because the published build has no `/api/v1/test/*` endpoints. It is tested against T03's `SqlServerFixture`.
- Record the machine spec in every result file. Budgets are for a dev laptop. ⏳ F30 re-runs the smoke profile once on B1/Azure SQL to set the real baseline.

## 3. Budgets (k6 thresholds; a breach fails the run)
| Endpoint / operation | p95 | Error rate |
|---|---|---|
| `GET /leads` page 1, 50k rows, default sort | < 500 ms | < 1% |
| `GET /leads` page 2000 / any sort key / `q=` search | < 1 s (`LIKE '%x%'` scan; parking-lot F13/F29.5) | < 1% |
| `GET /leads/{id}`, `GET /campaigns`, `GET /inbox/threads`, `GET /inbox/unread-count` | < 300 ms | < 1% |
| `GET /analytics/workspace/summary` (30 days) / campaign summary | < 800 ms | < 1% |
| `POST /auth/login` (15 VUs, under the rate limit) | < 400 ms | 0% non-429 |
| `POST /webhooks/sendgrid/events` 500-event batch | < 2 s | 0% |
| `POST /webhooks/sendgrid/inbound` | < 1 s (classification is async) | 0% |
| CSV 50k rows end to end (upload → Completed) | < 3 min | – |
| Sender job, 1k due enrollments, no AI | one run < 60 s (fits the minutely cron) | 0 duplicate sends |
| Discovery run, 500 leads (fake source) | < 60 s | – |

## 4. Files
### `tools/load/` (k6, TypeScript)
| File | Purpose |
|---|---|
| `lib/auth.ts` | Logs in seeded users once in `setup()`, shares tokens; refreshes on 401 |
| `lib/sendgrid-signing.ts` | Signs event batches with the perf environment's ECDSA private key (k6 `crypto` / `k6/experimental/webcrypto`) |
| `scenarios/smoke.ts` | 1 VU, 1 min, every endpoint above once. Runs in the regression pack (T09). |
| `scenarios/browse.ts` | 15 VUs ramping, 10 min: realistic mix (leads list/search/detail 50%, campaigns 15%, inbox + unread poll 25%, analytics 10%) with think time |
| `scenarios/webhook-burst.ts` | 20 batches × 500 events in 30 s, plus 50 inbound posts, while `browse` runs at 5 VUs (proves webhooks don't starve users) |
| `scenarios/soak.ts` | 5 VUs, 1 h: memory and connection-pool growth (watch `dotnet-counters`) |
| `scenarios/auth-burst.ts` | Asserts rate limits engage (429s expected) without 5xx |
| `run.ps1 -Scenario smoke` | Runs k6 with `--out json=results/<date>-<scenario>.json` and a summary export |

### `ZenLead.IntegrationTests/Performance/` (xUnit, `Category=Performance`, SQL Server)
- `LeadListQueryPlanTests`: on 50k leads, capture the SQL (EF interceptor) for each sort/filter, check for **index seeks** on the F13 indexes (`SET STATISTICS XML` / `sys.dm_exec_query_stats`), and assert a single query per page (no N+1 for company joins).
- `SenderJobThroughputTests`: 1k due enrollments → elapsed time, query count bounded (no per-enrollment lead/step reloads beyond the batch), 1k `EmailMessage` rows, 0 duplicates.
- `CsvImportThroughputTests`: 10k rows (F15.8 sanity, already in the plan) and 50k rows → time, peak working set below a loose bound (streaming proof).
- `EventBatchQueryCountTests`: 500 events → bounded query count (F28 already calls for this; run it on SQL Server here).
- `AnalyticsAggregateTests`: summaries on 100k `EmailMessage` rows meet the budget, using the `(WorkspaceId, CampaignId, SentAt)` index.

## 5. Front-end performance (Playwright, runs with T08)
- Lighthouse-style metrics via Playwright + `performance.getEntries()`: on the published build, the leads list with 50k leads has LCP < 2.5 s and route changes < 1 s.
- **Bundle budget:** `ng build` fails over the `angular.json` budget. Note the parking-lot F16 item: the budget was raised to 1.1 MB. The test plan tracks putting it back to 1 MB once `SharedModule` is split.
- Inbox polling: over 5 minutes with the tab hidden → zero `/unread-count` calls (F27 pause-when-hidden).

## 6. Output
`docs/testing/performance-baseline.md`: table of budgets vs measured, machine spec, commit SHA, any index or N+1 fixes raised as findings for F29.5.
