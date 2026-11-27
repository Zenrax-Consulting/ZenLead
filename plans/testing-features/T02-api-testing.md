# T02 — API Testing

**Branch:** `test/api` · **Size:** M · **Depends on:** T00 · **Partly waits on:** F24, F27, F28 endpoints

## Goal
Every `/api/v1` endpoint has HTTP-level tests for its contract: status codes, response shape, validation errors, paging, auth requirement and a cross-workspace case. There are two layers:
1. **In-process (xUnit + `ApiFactory`)**: fast, runs on every PR, and owns the detail.
2. **Black-box (Playwright `request` fixture, TypeScript)**: a thinner suite against a *running* API (E2E env or published build). It proves the real host (Kestrel, middleware order, static files, rate limiter partitions by real IP) behaves the same as the in-process one.

## 1. Endpoint inventory (single source of truth)
`ZenLead.Tests/Api/Inventory/EndpointInventory.cs` enumerates routes from `EndpointDataSource` at test time and compares them to a checked-in table `endpoint-inventory.json`: method, route, expected auth (`Anonymous | AnyUser | Tenant | SuperAdminOnly | WebhookSecret | WebhookSignature | UnsubscribeToken | HangfireCookie | E2EOnly`), and rate-limit policy.
- **New route not in the table → test fails** ("classify this endpoint"). This is the F29.3 authorization sweep, made continuous.
- T04 asserts the auth column. T02 asserts that each row has at least one contract test (a reflection or naming check: `[EndpointTest("GET /api/v1/leads")]` attribute on test methods).
- Extends the existing `AuthorizationMatrixTests` instead of duplicating it.

## 2. In-process contract tests (one file per controller, many exist already; fill the gaps)

| Area | Endpoints | Cases to cover (beyond what exists) |
|---|---|---|
| Auth | register, login, refresh, approvals approve/reject, forgot, reset, change-password, logout | Response shape (`accessToken`, `expiresIn`, no refresh token in the body if it travels another way — **verify** current shape); ProblemDetails on 400 with field errors; identical body and status for existing vs unknown email (register, forgot); 429 includes `Retry-After` |
| Workspaces (public picker) | `GET /workspaces?q=` | Max 20 results, only `{id,name}` fields (JSON property allow-list assertion), empty `q`, `q` with `%`/`_` |
| Admin | `GET/POST /admin/workspaces` | Duplicate name 409, bad email 400, list has no tenant content fields |
| Leads | list (all query params), get, create, update, delete, companies | Paging metadata, `pageSize` caps (0, negative, >max → 400 or clamped, documented), every sort key both directions, unknown sort → 400, filters combined, soft-deleted lead → 404, status transition 409, duplicate email 409, `If-Match`/concurrency if present (**verify**) |
| Import | upload, start, status, errors | Wrong content type 415/400, >10 MB 413, non-`.csv` 400, start twice 409, errors CSV `Content-Type: text/csv` + `Content-Disposition` |
| Discovery | target profiles CRUD, from-leads, runs, run status, credits | from-leads with 0 ids / other workspace's id; run with max over the cap → 400; run while another is running → 409 (**verify** rule) |
| Campaigns | list, create, update, steps CRUD + reorder, enroll, activate, pause, enrollments, `sender`, `activation-preview` (F24) | Activate with no steps / unresolvable token → 400 listing every problem; edit an active campaign's step (rule from F22.4); enroll skips counted per reason; activation-preview estimate matches `AiPricing` |
| Inbox (F27) | threads list/get/reply/reclassify, unread-count | Get marks read; reply on another workspace's thread 404; reclassify enum validation |
| Analytics (F28) | campaign summary, workspace summary | Date range: default 30 days, >366 → 400, `from>to` → 400; null rates on zero denominators serialised as `null` |
| AI | compose-email, token-usage | Budget exceeded → the documented status + error code the UI banner reads |
| Public | unsubscribe GET page / POST, `List-Unsubscribe-Post` one-click | Invalid token 400 with a generic page, already unsubscribed is idempotent 200 |
| Webhooks | inbound, events | Shape-level only here; signature and secret logic lives in T04/T05 |
| Ops | test-email, hangfire-access, health (if present) | AnyUser policy, cookie attributes `HttpOnly; Secure; SameSite=Strict; Path=/hangfire` |

**Cross-cutting assertions** (one helper, used everywhere): errors are `application/problem+json` with `traceId`; no stack traces or exception messages in 500 bodies (force one with a throwing fake); every JSON response is camelCase; dates are ISO-8601 UTC with `Z`.

## 3. OpenAPI contract snapshot
`OpenApiSnapshotTests`: fetch `/openapi/v1.json` from `ApiFactory`, normalise it (sort keys, strip descriptions) and compare to `ZenLead.Tests/Api/openapi.snapshot.json`. An unintended contract change fails the test. An intended one is accepted by re-running with `UPDATE_SNAPSHOTS=1`, which shows up in the PR diff. T10 uses the same document for the API↔UI check.

## 4. Black-box API suite (TypeScript, in `ZenLead.E2E/tests/api/`)
Uses Playwright's `APIRequestContext` (no browser). Shares fixtures with T07 (`apiClient`, `seed`, `mailbox`).
- `auth.api.spec.ts`: register → pickup approval email → approve → login → refresh rotation (old refresh token rejected after use) → logout.
- `tenancy.api.spec.ts`: two seeded workspaces. For every Tenant row in `endpoint-inventory.json` that takes an id, workspace B gets 404 on A's id (data-driven from the JSON).
- `rate-limits.api.spec.ts @security`: login burst from one client → 429 with `Retry-After`; picker burst → 429. Runs against the real host because partitioning by IP behaves differently in-process.
- `spa-fallback.api.spec.ts`: `GET /leads/123` → 200 `text/html`; `GET /api/v1/nope` → 404 problem+json, **not** `index.html` (a classic one-deployable bug).
- `webhooks.api.spec.ts`: posts captured inbound/event fixtures with the right and wrong secret/signature (signature generated in TS with Node `crypto` P-256 against the E2E public key).

## 5. `ZenLead.Api.http`
Keep the manual file in step: one request block per inventory row, grouped by feature, with `@token` variables. A small test (`HttpFileCoverageTests`) parses the file and lists inventory rows it doesn't mention. It warns rather than fails.

## Run
- `dotnet test ZenLead.Tests --filter FullyQualifiedName~ZenLead.Tests.Api`
- `cd ZenLead.E2E; npx playwright test tests/api` (needs the E2E host from T07's global setup)
