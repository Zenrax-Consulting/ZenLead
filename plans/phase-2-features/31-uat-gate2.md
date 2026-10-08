# Feature 31 — UAT & Gate 2 Readiness

**Branch:** `chore/uat-gate2` (docs, one small tool, config toggles; bug fixes found during UAT go on their own `fix/…` branches)
**Sprint:** 5, after F30 is live on the custom domain
**Depends on:** everything. This feature adds **no product functionality** — it proves the product, records the evidence, and produces the Gate 2 decision.

## Goal
Run the Gate 2 loop — *super admin creates the workspace → register & approve → discover/import leads → build a campaign (with an AI step) → activate → receive the email → reply → see the classified thread → read analytics* — on **production Azure**, with **at least two Zenrax users** (the owner and one colleague) each using their **own mailbox** in the **shared Zenrax workspace**; fix everything that matters; check cost and the Gate 2 criteria; rehearse; decide.

## Files to add (all under `docs/` unless stated)

| File | Purpose |
|---|---|
| `uat-script.md` | The numbered script testers follow (below) |
| `uat-results.md` | Results log: one row per step per tester |
| `smoke-checklist.md` | One-page pre-release checklist (31.2) |
| `cost-audit.md` | Actuals vs the §1a target (31.3) |
| `gate2-rehearsal.md` | Timed run sheet + demo talk track (31.4) |
| `gate2-decision.md` | Criteria with evidence links, decision, Phase 3 backlog |
| `phase-3-backlog.md` | Everything triaged out of Phase 2 |
| `tools/smoke/smoke.mjs` + `package.json` + `README.md` | Read-only API smoke script (31.2) |
| `.gitignore` | add `docs/gate2-evidence/` (screenshots/exports kept locally, like Gate 1) |

## 31.0 — Preparation (do before inviting testers)

1. **Test mailboxes.** Each tester has: their own work mailbox (the Zenrax identity used to register) and **3–5 "prospect" mailboxes they control** on different providers (a Gmail, an Outlook/M365, one on a custom domain, one that sets an out-of-office). Prospect addresses are what campaigns are sent to — **never real prospects during UAT**.
2. **UAT must never email real prospects (owner requirement).** Production runs with `Sending__Mode=Restricted` and `Sending__RecipientAllowList` set to the testers' own prospect mailboxes/domains only; F23 enforces this fail-closed (empty list = nothing sent). Leads from discovery or CSV may exist in the workspace, but they cannot be mailed. Enrol only the tester-controlled addresses. Verify before UAT starts: enrol one non-allow-listed dummy lead in a throwaway campaign and confirm it is blocked with the allow-list reason. `Mode=Live` is switched on **only** as a go-live action after the Gate 2 decision.
3. **Real discovery run:** the parent plan requires one real run against the chosen lead provider's free/test tier before Gate 2 (Sprint 1 exit note). Decide the provider (parent §1), add its key to Key Vault, set `LeadSource__Provider`, keep `MaxLeadsPerRun` small (≈ 10) and the monthly credit cap low.
4. **Baselines:** snapshot the cost dashboards (Azure Cost Analysis, SendGrid usage, OpenAI usage, free-SQL vCore-seconds) so UAT's own spend is visible.
5. Evidence folder `docs/gate2-evidence/` (git-ignored) with a subfolder per tester; screenshot or screen-record each section.

## 31.1 — The UAT script (`uat-script.md`)

Each step: **Action → Expected result → Evidence**. IDs are stable (`UAT-3.4`) so `uat-results.md` and bug tickets can reference them. Testers: **A** = owner (workspace admin), **B** = colleague.

| # | Step | Expected |
|---|---|---|
| **1 Platform admin** | | |
| 1.1 | Super admin signs in at `https://leads.zenraxconsultancy.com` | Lands on *Workspaces*; no leads/campaigns/inbox/analytics in nav |
| 1.2 | Create workspace **Zenrax** with admin email = A's address | Appears in the list (0 users); A receives "workspace ready" email |
| 1.3 | Super admin opens `/leads` directly and calls an API URL with their token | Redirected to *Workspaces* / API returns 403 |
| **2 Accounts** | | |
| 2.1 | A registers: types "zen", picks **Zenrax**, submits | "Request sent" screen; A cannot sign in yet (message: awaiting approval) |
| 2.2 | A opens the approval email (sent to A's own address), reviews the card, **Approves** | "Approved" confirmation; approval email to A; A can sign in. Re-opening the link says *already used* |
| 2.3 | B registers into Zenrax; A approves via the emailed link | B can sign in and sees the **same** (empty) workspace |
| 2.4 | B uses *Forgot password* → emails → resets → signs in with the new password; a second browser session of B is signed out | Old password rejected; neutral wording throughout; link single-use |
| 2.5 | Register an unknown email into Zenrax; also request approval for an address that already exists | Same neutral response in both cases; no extra emails beyond the allowed re-send |
| **3 Leads** | | |
| 3.1 | A creates a **target profile** from scratch and another **from an existing lead**; runs discovery for 10 leads | Run completes; leads tagged *Discovery*; "View imported leads" filters correctly; credit meter moves |
| 3.2 | Re-run the same profile | Mostly duplicates; no duplicate leads; summary numbers add up |
| 3.3 | B imports a 100-row CSV prepared with: duplicates, bad emails, mixed case, a `;` delimiter variant, an unsubscribed address, a non-UTF-8 name, a formula cell (`=1+1`) | Counts reconcile; issues CSV downloads and opens in Excel with no formula executing; re-upload imports 0 |
| 3.4 | Both users see each other's leads; edit a lead, try an illegal status change, delete a lead | Shared data; illegal change refused with a clear message; deleted lead disappears from the list |
| 3.5 | Add each tester's prospect addresses as leads (company/title filled) | Ready for the campaign |
| **4 Campaign** | | |
| 4.1 | A creates a campaign: 3 steps (delay 0/2/4), tokens incl. a fallback, **step 2 with AI personalisation**; previews with a lead that has no company | Timeline shows Day 0 → Day 2 → Day 6; preview warns about the missing value |
| 4.2 | Set window/days/time zone; for the demo shorten delays to minutes **by editing `NextSendAt` via the supported admin step** in `uat-script.md` appendix (SQL snippet run by the owner) or use a 0-day delay on a throwaway campaign | Window summary correct in the workspace zone |
| 4.3 | Enroll the prospect leads: preview first | Counts and warnings (unverified/risky) shown; confirm adds them |
| 4.4 | Activate | Estimated AI cost shown; status Active; (if the domain flag is off, the specific problem is explained) |
| **5 Delivery** | | |
| 5.1 | Step 1 arrives in every prospect mailbox | Within ~2 min of activation; record **inbox vs spam** per provider |
| 5.2 | View the raw headers of one message per provider | DKIM `pass` for `zenraxconsultancy.com`, SPF/DMARC aligned, `List-Unsubscribe` + `List-Unsubscribe-Post`, `Reply-To: r-<guid>@reply.leads…`, unsubscribe link in the footer |
| 5.3 | Follow-up steps fire on schedule; the AI-personalised step reads sensibly and still has the footer | No duplicate sends; one email per lead per step |
| 5.4 | One prospect clicks the unsubscribe link, confirms; Gmail's one-click *Unsubscribe* is used on another | Lead `Unsubscribed`; sequence stops; later enrolment attempts skipped as suppressed |
| **6 Replies** | | |
| 6.1 | Reply "Sounds interesting, can you send pricing?" | Appears in the Inbox ≤ ~1 min, threaded to the right lead, unread badge, classified *Interested* |
| 6.2 | Reply "not for us" / "please remove me" / set an OOO / reply from a *different* address | *Not interested*; *Unsubscribe* → lead suppressed; *OOO* → sequence continues/resumes; different-address reply flagged |
| 6.3 | Answer from the Inbox composer; the prospect replies again | Arrives in the same mail thread (`Re:`), next reply lands in the same inbox thread; double-click Send sends once |
| 6.4 | Manually reclassify a thread; confirm an AI "needs review" item | Effects match the label; stays consistent after refresh |
| 6.5 | Paste `<script>alert(1)</script>` into a reply | Rendered as text |
| **7 Analytics** | | |
| 7.1 | Open Analytics and the campaign view | Sent/Delivered/Replied match what the testers actually did (counted by hand); Opened labelled approximate |
| 7.2 | Change the date range; view as table | Consistent numbers; table equals chart |
| **8 Isolation & safety** | | |
| 8.1 | Super admin creates a second workspace **UAT-Other** with a second user (approved); that user tries every Zenrax URL/ID they can find (lead id, campaign id, thread id) in the UI and via the API | Always 404/empty; never Zenrax data |
| 8.2 | Anonymous calls to a few API endpoints; pending user login | 401 / awaiting approval |
| 8.3 | Wrong password 5× | Lockout with the same generic message |
| **9 Operations** | | |
| 9.1 | Restart the web app (`az webapp restart`) while a campaign is mid-run | No duplicate emails; sender resumes; job dashboard shows recurring jobs |
| 9.2 | Alerts: trigger one test alert (e.g. temporarily break the webhook secret → webhook failure alert) | Email received; secret restored |
| 9.3 | Cost/limits glance | Azure + SendGrid + OpenAI consumption within expectation; AI usage rows present |

**Appendix in `uat-script.md`:** the exact read-only SQL for counting expected emails/events, the supported way to shorten delays for a demo, and how to reset UAT data (delete the UAT leads/campaigns in the UI; the **UAT-Other** workspace and test users are left in place or removed by the owner via SQL — no delete UI exists in Phase 2).

### Results log and triage (`uat-results.md`)
```markdown
| ID | Tester | Date | Result (Pass/Fail/Blocked) | Severity | What happened | Evidence | Ticket/PR |
```
Severity: **P0** — data leak/cross-workspace access, double-send, lost reply, security hole, can't complete the loop; **P1** — wrong numbers, broken main-flow step, misleading UI that causes wrong action; **P2** — polish/papercuts. **P0 and P1 are fixed in this sprint** (each as a `fix/…` branch + PR + regression test where it makes sense) and the failed step is **re-run by a different tester**; P2 goes to `phase-3-backlog.md`. Nothing is closed without a re-test row.

## 31.2 — Smoke path (`smoke-checklist.md`) — re-run before every release (parent §9)

One page, ~15 minutes, written so anyone can run it. Sections: **(a) automated** — `node tools/smoke/smoke.mjs https://leads.zenraxconsultancy.com` (read-only: `/health` 200; security headers present; anonymous `GET /api/v1/leads` → 401; login with a dedicated smoke user from env vars → list leads → 200 and empty-or-normal; super-admin-only route as that user → 403; static SPA `index.html` and a deep link `/leads` return HTML; response header `X-Correlation-Id` present) — never creates data, never logs tokens; **(b) manual** — sign in; open leads list; open a lead and generate a draft (AI works, budget shown); open a campaign and its steps; send one email via the Inbox composer to a test mailbox (**not** a campaign send); open Analytics; `/hangfire` via the ops menu shows the recurring jobs green; **(c) post-deploy** — App Insights shows no new error spike; migrations applied (latest migration id in `__EFMigrationsHistory` equals the commit's); alerts not firing.
`tools/smoke/` follows the shape of `tools/gate1-rehearsal` (Node ≥ 20 `fetch`, no browser, exit code non-zero on any failure, JSON summary written to `docs/gate2-evidence/`).

## 31.3 — Cost audit (`cost-audit.md`)

Collect **actuals** for the UAT period and project to steady state; compare with the re-baselined target (parent §1a: ≈ $35–50/month incl. SendGrid, plus OpenAI usage; Azure alone ≈ $15–25).

| Line | Source | UAT actual | Projection (steady state) | Target | Verdict |
|---|---|---|---|---|---|
| App Service B1 | Cost Analysis | | | $13.14 | |
| Azure SQL | Cost Analysis + free-offer vCore-seconds metric | | | $0 (fallback $5) | |
| Storage + Key Vault | Cost Analysis | | | < $1 | |
| App Insights / Log Analytics | Cost Analysis + daily-cap hits | | | $0–3 | |
| SendGrid plan | SendGrid billing | | | from $19.95 | |
| OpenAI | dashboard **and** `AiUsageLogs` | | | usage-based | |
| Lead provider credits | provider dashboard + `LeadDiscoveryRuns.CreditsUsed` | | | per vendor tier | |
| **Total / month** | | | | **≈ $35–50 + usage** | |

Read-only SQL appendix (run against production by the owner; **no tenant content is selected**): monthly AI cost by `Purpose` and by model (`SELECT Purpose, Model, COUNT(*), SUM(PromptTokens), SUM(CompletionTokens), SUM(EstimatedCostUsd) FROM AiUsageLogs WHERE CreatedAt >= @monthStart GROUP BY Purpose, Model`), emails per day (`COUNT(*)` from `EmailMessages` by `CAST(SentAt AS date)`), discovery credits per month. Projection method: AI cost per sent email = (personalisation + classification spend) ÷ emails sent in UAT, multiplied by expected monthly volume; call out where actual ≠ the parent plan's rough figures (GPT-4o vs the cheaper classifier model). Record whether the SQL free offer held up (vCore-seconds trend) and recommend **Basic** if not. Any line above target ⇒ a recommendation in `gate2-decision.md`.

## 31.4 — Gate 2 rehearsal, live demo, decision

1. **Rehearsal on a clean workspace** (`docs/gate2-rehearsal.md`): the super admin creates **Gate2-Demo** (fresh data, nothing from UAT); two people run the *entire* loop end to end against the stopwatch; record timings for each stage (registration→approval, discovery run, import, activation→first email, reply→inbox visible, classification, analytics). Pre-send step 1 a few minutes before the demo slot where timing matters (email latency is the only uncontrollable step), keep a **recorded run (screen capture)** as the fallback if mail is delayed, and prepare the demo accounts/mailboxes in advance. Fix anything that breaks (P0/P1 rules apply) and rehearse again until two consecutive clean runs.
2. **Live demo** (≤ 20 min; order mirrors the Gate 2 loop): platform admin creates the workspace → registration/approval → import + discovery → campaign with AI step → activate → email arrives live → reply from a phone → inbox thread appears classified → analytics. Talking points: multi-tenant isolation proof (the second workspace test), safety rails (suppression, allow-list, caps, at-most-once sending), cost position vs target.
3. **Decision record** (`gate2-decision.md`):

| Gate 2 criterion (parent §7) | Evidence | Met? |
|---|---|---|
| MVP deployed to Azure, reachable outside the local network (custom domain, HTTPS) | `docs/prod-parity-checklist.md`, screenshot of the live URL + certificate | |
| ≥ 2 Zenrax users in the shared workspace completed discover/import → campaign → send → reply in inbox → analytics | `uat-results.md` rows for A and B (steps 3–7) | |
| Infrastructure spend within the re-baselined budget (≈ $35–50/month incl. SendGrid + OpenAI usage) | `cost-audit.md` | |
| (Internal) cross-workspace access denied; all non-public endpoints require auth; unapproved users can't sign in | isolation tests green in CI + UAT 8.x + `docs/api-authorization-matrix.md` | |

Then: **open P0/P1 count (must be 0)**, open P2 list, the decision (**Go / Conditional go (conditions + dates) / No-go**), who decided and when, **go-live actions** (set `Sending__Mode=Live` (and clear `Sending__RecipientAllowList`), final secret rotation if any secret was shared during UAT, confirm budget alerts reach the owner, confirm backups/PITR verified, update `CLAUDE.md` "Project state" and the Phase 2 plan §0, tag the release), and the **Phase 3 backlog** (`phase-3-backlog.md`), seeded from the parent plan's deferred list (roles/invites/members UI, multi-workspace switching, per-workspace senders, MFA/SSO, scheduled discovery, real-time inbox, billing, GDPR tooling…) plus everything UAT surfaced (with the tester's evidence).
4. After the decision: tag `v0.2.0-mvp`, archive the evidence folder outside the repo, close the sprint in the plan files (update parent plan status lines).

## Tests / automation in this feature
Mostly manual by design. Automated: `tools/smoke/smoke.mjs` (run locally against staging-like config and against production), and **regression tests added with every P0/P1 fix** (the rule is in the triage section). `dotnet test` and `ng test` stay green on `main` throughout (CI gate from F30).

## Not in this feature
New features, load testing beyond F29's baseline, penetration testing, onboarding anyone outside Zenrax, public sign-up, billing, user-facing documentation/help centre, a status page.

## Exit criteria (mirror of the decision table)
All four Gate 2 criteria checked with evidence links, zero open P0/P1, cost audit complete, smoke checklist run once on the final build, rehearsal done twice cleanly, decision recorded, Phase 3 backlog written.
