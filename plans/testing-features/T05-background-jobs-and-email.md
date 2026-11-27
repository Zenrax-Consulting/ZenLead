# T05 — Background Job & Email Testing

**Branch:** `test/jobs-email` · **Size:** L · **Depends on:** T00, T03 (`SqlServerFixture`, `HangfireTestServer`, `ProviderStubs`) · **Waits on:** F25, F26, F28 for those sections

## Goal
Prove the four Hangfire jobs (lead discovery, CSV import, campaign sender, reply classification) and every email path (transactional, campaign, inbound, events, unsubscribe) are **correct, idempotent, resumable and safe**, including when a job crashes. Then do one controlled live round trip through real SendGrid in Restricted mode.

## 1. Job inventory
| Job | Trigger | Key properties to prove |
|---|---|---|
| `ProcessLeadDiscoveryJob(runId, workspaceId)` | User starts a run | Resumable, re-run makes no duplicates, cap → `CapReached`, provider failure → backoff then `Failed`, no ambient workspace |
| `ProcessCsvImportJob(batchId, workspaceId)` | `start` | Checkpointed batches of ~500, resume after crash, re-run imports 0, counters reconcile, error log capped |
| `CampaignSenderJob` (recurring, minutely, `[DisableConcurrentExecution]`) | Cron | Insert-before-send, at-most-once, daily cap, per-minute throttle, send window/time zone, suppression, Restricted allow-list, never blocks other enrollments |
| `ClassifyReplyJob` (F26) | Enqueued by inbound | Exactly once per human reply, cap → `Unclassified`, OOO resumes the sequence, unsubscribe auto-actions above the confidence threshold |

## 2. Job tests on real Hangfire (`ZenLead.IntegrationTests/Jobs/`, Category=Integration)
Run the job through `HangfireTestServer` (real serialisation, retries, storage), not just `ExecuteAsync` directly. Direct calls are already covered by unit tests.

- **Serialisation:** each job enqueues with primitive args only and round-trips through SQL storage (catches a DI-only parameter sneaking in).
- **Retry policy:** a job throwing a transient error retries per F14.0 config (3 attempts, exponential), then lands in Failed. Dead-letter is visible in the monitoring API.
- **Crash/resume (parking-lot F14 and F15 "kill the API mid-run"):** a `CrashAfter(n)` seam in the job store (test-only decorator) throws after batch *n* commits. Re-enqueue → final state equals an uninterrupted run: lead count = valid unique rows, no duplicates. **Assert and document the known undercount** (`ImportedCount` counts the replayed batch as duplicates, parking-lot F14/F15). The test pins today's behaviour, so changing it is a deliberate choice.
- **Stale `Running` run** (parking-lot F14): a run left `Running` with no Hangfire job → a new run is blocked. Pin the current behaviour, and file a finding if no timeout exists.
- **Concurrency:** start the sender job twice at once on two workers → one executes, one is skipped by `DisableConcurrentExecution`; the DB unique `(EnrollmentId, StepId)` holds even with the attribute removed (seam test).
- **No ambient tenant:** every job runs with `ICurrentWorkspace = null`, and data still lands in the right workspace via `ForWorkspace`. A job given workspace B's id with A's run id → no-op plus a warning.

## 3. Sending engine scenarios (F23, deeper than the existing `SendDueEmailsUseCaseTests`)
Table-driven on SQL Server with `FakeTimeProvider` and the WireMock SendGrid stub:
| Scenario | Expected |
|---|---|
| 3-step campaign, delays 0/3/7, workspace TZ `America/New_York`, window 09:00–17:00 | Sends at the right UTC instants across the **DST change** (2026-03-08 and 2026-11-01) |
| Due outside the window | Pushed to the next window opening, not skipped |
| Daily cap 50, 120 due | 50 today, the rest roll to tomorrow in `NextSendAt` order |
| SendGrid 429 | Transient → enrollment stays due, retried next run, still one `EmailMessage` |
| Crash after the SendGrid 202 but before the DB commit | Message stays `Queued`/uncertain → **not resent** (at-most-once, deviation #12). Documented in the test name. |
| Lead unsubscribed / bounced / in `SuppressedEmail` between enrollment and send | Skipped, enrollment stopped with the right status |
| Lead with missing `{{firstName}}` and no fallback | Skipped with a reason, no "Hi ," email (deviation #10) |
| AI personalisation step, budget exhausted | Falls back per the F23 rule (**verify**: skip vs send the template) and logs it |
| **`Sending:Mode=Restricted`**, recipient not on the allow-list | Not sent, recorded as restricted. **Empty allow-list sends nothing.** |
| Reply arrives (F25) between steps | Enrollment `Replied`, step 2 never sends |
| OOO reply (F26) | Sequence pauses, then resumes per `EnrollmentRules` |

## 4. Email content & transport (unit + pickup)
- **Transactional (F18–F21):** approval, "you're approved", password reset, password-changed notice, workspace-ready (if built), test email. For each: recipient, subject, link uses `App:PublicBaseUrl`, the token in the link works exactly once, a text part exists, no PII beyond what's needed. Snapshot the rendered body (`Verify`-style approved files under `ZenLead.Tests/Fixtures/EmailSnapshots/`).
- **Campaign mail (F23):** `List-Unsubscribe` + `List-Unsubscribe-Post` headers, an unsubscribe link in the body, `Reply-To` encodes the thread token (`ReplyAddressTests` exists, so extend it to the full payload), `Message-ID` stored for threading, custom args for event correlation.
- **Guard test `NoTestConfigEmailsRealPeople`:** scans `appsettings*.json`, `ApiFactory`/`IntegrationApiFactory` settings and `ZenLead.E2E` env files. Any config that selects SendGrid must also have `Sending:Mode=Restricted` and an allow-list containing only `example.test` or entries from `tools/testing/allowed-test-recipients.txt`.

## 5. Inbound & events (F25, F26, F28), fixture-driven
- **Fixtures:** captured with `Webhooks:CaptureRaw=true` during the T05 live run (§6), with personal data replaced: Gmail, Outlook/O365, Apple Mail, OOO, DSN bounce, non-UTF-8, with attachment, HTML-only. Event batches: delivered, open ×2, bounce hard/soft, dropped, spamreport, deferred, processed, out of order.
- **Threading levels:** Reply-To token match → `In-Reply-To`/`References` match → sender-email match → quarantine. One fixture per level, plus the "never silently dropped" case.
- **Duplicates:** same inbound Message-ID twice → one message, classification enqueued once (F26 extension). Same event batch twice → no state change.
- **End to end in-process:** sender job sends step 1 (WireMock) → inbound fixture rewritten with that message's Reply-To and Message-ID → posted to the webhook → classify job (fake classifier) → event batch for that `emailMessageId` → analytics summary shows sent 1 / delivered 1 / opened 1 / replied 1. **This one test walks the whole Gate 2 email loop without the network.**

## 6. Live email round trip (manual, scripted, `Category=Live`, `docs/testing/email-live-checklist.md`)
Run once per release candidate. Record evidence in `docs/testing/evidence/<date>-email-live/`.
1. Preconditions: `doctor.ps1` green; `Email:Provider=SendGrid`; **`Sending:Mode=Restricted`, allow-list = tester mailboxes only**; dev tunnel up; Inbound Parse and Event Webhook URLs point at the tunnel; `Webhooks:CaptureRaw=true`.
2. Deliverability: send to `mail-tester.com` (or a Gmail/Outlook seed box) → record the SPF/DKIM/DMARC pass and spam score; headers show `zenraxconsultancy.com` DKIM alignment (F17).
3. Transactional: register a tester → approval email arrives → approve → reset password → notice arrives.
4. Campaign: 2-step campaign with step delays shortened (step 2 via clock or 1-minute delay in a test campaign) to 2 tester mailboxes + 1 non-allow-listed address → the non-allow-listed one is **not** sent (check the SendGrid activity feed too).
5. Reply from Gmail and from Outlook → thread appears within ~1 min, correctly threaded and classified; reply "please unsubscribe me" → enrollment paused and suppression row added.
6. Open the email → event webhook updates `OpenedAt`; the dashboard moves.
7. Click unsubscribe in the email → landing page → POST → lead `Unsubscribed`; Gmail's one-click unsubscribe also works.
8. Save the captured raw payloads as new fixtures (scrubbed) for §5.

## Run
```
dotnet test ZenLead.IntegrationTests --filter "FullyQualifiedName~Jobs|FullyQualifiedName~Email"
dotnet test ZenLead.Tests --filter "FullyQualifiedName~Inbound|FullyQualifiedName~Events|FullyQualifiedName~Sending"
```
