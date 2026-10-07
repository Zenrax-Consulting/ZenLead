# Feature 24 — Campaigns UI

**Branch:** `feature/angular-campaigns`
**Sprint:** 3 (the UI can start against the F22 API on day one of the sprint; it is the first thing cut if Sprint 2 overran — see parent plan)
**Depends on:** F22 (API), F23 for real counts/`sent` and the activation→send loop, F13 (`LeadSelectionService`, list filters), F12 (shell, lazy routes), F18 (`PUT /workspaces/current/time-zone`).

## Goal
Everything needed to run a campaign without touching the API by hand: list, create/edit, **step builder** (reorder, delays, token helper, preview with a real lead, "Generate with AI", send timeline), **enrollment** (from the leads list, with a dry-run preview of what will be skipped), activate/pause with validation errors shown, and a sender-domain status indicator.

## Small backend additions (needed by this UI; each has a test)
All in `CampaignsController`/`CampaignDtos` from F22:
- `CampaignResponse` gains `int StepsStarted` (= `MaxNextStepOrder − 1`, i.e. how many steps any enrollment has passed) so the builder can disable delete/reorder **before** the server says 409.
- **`GET /api/v1/campaigns/sender`** → `{ fromAddress, fromName, domainVerified, inboundDomain }` from `EmailOptions` (no secrets).
- **`GET /api/v1/campaigns/{id}/activation-preview`** → `{ enrolledActive, aiSteps, estimatedAiCalls, estimatedAiCostUsd, aiBudgetRemainingUsd }`. Estimate = `enrolledActive × aiSteps` calls × the cost of an assumed 600-in/300-out call from `AiPricing.EstimateCostUsd` (constant documented in code). Powers "this will make ~N AI calls, about $X" on activation (parent risk table).
- `CampaignListItem` gains `Sent` (count of `EmailMessage` with `Sent…Bounced`, one grouped query).

## Files to add (all under `ZenLead.Client/src/app/`)

### Module & routing
**`features/campaigns/campaigns-module.ts`**, **`campaigns-routing-module.ts`** (lazy; replaces the `ComingSoon` stub child in `app-routing-module.ts`)
```ts
const routes: Routes = [
  { path: '', component: CampaignList },
  { path: ':id', component: CampaignDetail }      // tabs: Steps | Leads | Settings (query param ?tab=)
];
```
`app-routing-module.ts`: `{ path: 'campaigns', loadChildren: () => import('./features/campaigns/campaigns-module').then(m => m.CampaignsModule) }` (under the shell, `tenantGuard`). `environments`: `features.campaigns = true`. `SharedModule.MATERIAL` gains `MatTabsModule`, `MatDialogModule`, `MatSnackBarModule`, `MatChipsModule`, `MatSlideToggleModule`, `MatButtonToggleModule`, `MatTimepicker`-free `<input type="time">`, `DragDropModule` (`@angular/cdk/drag-drop`), `MatDividerModule`.

### Data layer
**`campaigns.models.ts`** — mirrors the F22 DTOs: `CampaignStatus`, `EnrollmentStatus`, `SendDays` (bit flags as numbers + helper `daysToSet/ setToDays`), `Step`, `Campaign`, `CampaignListItem`, `EnrollResult`, `EnrollmentRow`, `ActivationProblem`, `ActivationPreview`, `SenderStatus`, `PreviewResponse`.
**`campaigns.service.ts`** — `list()`, `get(id)`, `create(req)`, `update(id, req)`, `addStep`, `updateStep`, `deleteStep`, `reorderSteps(id, orderedIds)`, `enroll(id, { leadIds | filter, dryRun })`, `enrollments(id, status?, page, pageSize)`, `activate(id)`, `pause(id)`, `activationPreview(id)`, `preview(id, leadId, stepId)`, `senderStatus()`. Errors keep `error.problems` / `error.code` intact for the components.

### Pure helpers (unit-tested, no Angular)
**`features/campaigns/schedule-display.ts`** — the "send timeline" logic the parent plan asks to be unit-tested:
```ts
export interface TimelineItem { order: number; day: number; label: string; }

/** Cumulative day offsets: step 1 is "Day 0"; each later step adds its delay to the previous step's day. */
export function buildTimeline(steps: { order: number; delayDays: number }[]): TimelineItem[] {
  let day = 0;
  return [...steps].sort((a, b) => a.order - b.order).map((s, i) => {
    day += i === 0 ? 0 : s.delayDays;
    return { order: s.order, day, label: `Day ${day}` };
  });
}
export function timelineSummary(steps: { order: number; delayDays: number }[]): string {
  return buildTimeline(steps).map(t => t.label).join(' → ') || 'No steps yet';      // "Day 0 → Day 3 → Day 7"
}
/** Honest copy about the window: delays are calendar days in the workspace zone; sends happen inside the window on allowed days. */
export function windowSummary(start: string, end: string, days: number, tz: string): string { /* "Mon–Fri, 09:00–17:00 (Asia/Dubai)" */ }
```
**`features/campaigns/token-insert.ts`**
```ts
export const TOKENS = [
  { token: '{{firstName}}', label: 'First name' }, { token: '{{lastName}}', label: 'Last name' }, { token: '{{name}}', label: 'Full name' },
  { token: '{{company}}', label: 'Company' }, { token: '{{title}}', label: 'Job title' }
];
/** Pure: where does the caret land and what is the new text when inserting at the current selection. */
export function insertAt(value: string, start: number, end: number, text: string): { value: string; caret: number } {
  return { value: value.slice(0, start) + text + value.slice(end), caret: start + text.length };
}
export function unknownTokens(template: string): string[] { /* same regex/allow-list as the server; shown as inline warnings while typing */ }
```
**`features/campaigns/format.ts`** — `formatInZone(iso, tz)` via `Intl.DateTimeFormat(undefined, { timeZone, dateStyle: 'medium', timeStyle: 'short' })`; `statusChip(status)` → `{ label, tone }` (tone drives colour **and** an icon/text — never colour alone).

### Components
**`campaign-list`** — table (name, status chip, steps, enrolled, sent, created), row click → detail; **New campaign** → small dialog (name) → `create` → navigate to `/campaigns/:id`; empty state "Create your first campaign"; error + retry.

**`campaign-detail`** (`mat-tab-group`, active tab in `?tab=`):
- Header: name, status chip, timeline summary (`timelineSummary`), **sender status chip** (`SenderStatus.domainVerified` ✔ "Sending domain verified" / ⚠ "Sending domain not verified" → opens `sender-help-dialog`: short static steps — add the SendGrid DNS records for `leads.<domain>`, wait, set `Email:DomainVerified`, with the inbound MX note), **Activate** / **Pause** buttons (state-dependent).
- **Activate flow:** `activationPreview` → confirm dialog showing enrolled count, the timeline, the window, and — if `aiSteps > 0` — "~N AI calls, about $X (budget left $Y)" → `activate`. A `409` with `problems[]` opens `activation-problems-dialog`: a list of messages, each with a **Fix** link that selects the Steps tab and scrolls to/focuses that step (`problem.code` + step order parsed from the response: extend F22's problem to carry `stepOrder?: number`). The `domain_unverified` problem opens the help dialog.
- Running-campaign banner (when `status != Draft`): "Edits to subjects/bodies apply to emails not yet sent. Steps that have already gone out can't be deleted or reordered."

**`steps-tab` + `step-editor`**
```ts
// steps-tab: CDK drag-and-drop list; each step is a card (step-editor) with a drag handle
drop(event: CdkDragDrop<Step[]>): void {
  if (this.locked(event.previousIndex) || this.locked(event.currentIndex)) { this.snack.open('Steps that have already been sent can’t be reordered.'); return; }
  const next = [...this.steps]; moveItemInArray(next, event.previousIndex, event.currentIndex);
  this.steps = next;                                                          // optimistic
  this.service.reorderSteps(this.campaign.id, next.map(s => s.id)).subscribe({ error: e => { this.reload(); this.showError(e); } });
}
locked(index: number): boolean { return this.campaign.stepsStarted > 0 && index < this.campaign.stepsStarted; }   // sent steps are pinned
```
`step-editor` (reactive form per card, autosave on blur / debounced 800 ms with a visible "Saved ✓ / Saving… / Couldn't save — retry" indicator, `PUT` per step):
- **Delay** (`number`, 0–90; first step shows "Sends right away" and is read-only), live timeline label ("Sends on Day 3, between 09:00–17:00 Mon–Fri").
- **Subject** and **Body** (`matInput`, textarea autosize). **Token toolbar**: chips for each token; clicking inserts at the caret (`insertAt` + restore selection via `setSelectionRange` in the next microtask); the `{{firstName|there}}` fallback syntax is explained in a tooltip. Inline warnings: unknown tokens (`unknownTokens`) and "uses {{company}} — leads without a company will be skipped unless you add a fallback".
- **Footer note** (locked, not editable): "An unsubscribe link is added automatically."
- **Preview with lead**: lead autocomplete (`LeadsService.list({ q, pageSize: 10 })`) → `service.preview(campaignId, leadId, stepId)` → rendered subject/body in a read-only panel with `missingTokens` highlighted ("This lead has no company — the email would be skipped"). The preview endpoint is the server renderer, so there is exactly one implementation.
- **Generate with AI** (reuses `LeadsService.composeEmail(leadId, context)`): needs a preview lead selected; optional one-line instruction input ("mention our Dubai office"); fills **subject/body** (user can edit, nothing is saved until the normal autosave); on `429 ai_budget_exceeded` shows the budget message. A **"Personalise each email with AI at send time"** toggle (`useAiPersonalisation`) with a note on cost and fallback ("If AI is unavailable the template is sent as written").
- **Delete** with confirm; disabled when `locked`. **Add step** appends a new step (delay defaults to 3).

**`enrollment-tab`**
- Toolbar: **Add leads** → navigates to `/leads?enrollIn=<campaignId>`; status filter select; counts strip (Active / Completed / Replied / Unsubscribed / Bounced / Failed).
- Table (`mat-table`, paging): lead name (link), email, **"Step 2 of 3"**, **next send** (`formatInZone(nextSendAt, workspaceTz)`, "—" when none / paused campaign shows "Paused"), status chip, failure reason (truncated, tooltip). Empty state with the **Add leads** button.

**`enroll-dialog`** (opened from the leads list in `enrollIn` mode)
```ts
// input: { campaignId, campaignName, leadIds?: string[], filter?: LeadQuery, total: number }
ngOnInit(): void { this.loading = true; this.service.enroll(this.data.campaignId, this.payload(true)).subscribe(r => { this.preview = r; this.loading = false; this.cdr.markForCheck(); }); }  // dryRun:true
confirm(): void { this.service.enroll(this.data.campaignId, this.payload(false)).subscribe({
  next: r => this.dialogRef.close(r),
  error: e => { this.error = e.status === 409 ? 'This campaign is completed.' : 'Could not add leads.'; this.cdr.markForCheck(); } }); }
```
Shows a clear breakdown: **Will be added N**, already in this campaign, skipped (unsubscribed/bounced, invalid email, missing a value used in your templates), and warnings (**N have unverified emails, M are risky** — "These may bounce and hurt deliverability"; checkbox "Add them anyway" is *not* offered — enrollment of risky/unverified is allowed by F14.7, the dialog only informs). `truncated` → "Only the first 5,000 were considered." **Confirm** is disabled while `Will be added = 0`.

**`leads-list` changes (F13 file, modified)**: `?enrollIn=<id>` puts the list in *enroll mode* — a sticky bar "Adding to **<campaign>** — N selected · **Add selected** · **Add all M matching this filter** · Cancel" (reads `LeadSelectionService`; "all matching" sends `filter = selection.lastQuery`, only offered when `lastTotal ≤ 5000`); outside enroll mode, the selection bar's **Add to campaign** button (flag `features.campaigns`) opens a `campaign-picker-dialog` (list of Draft/Active/Paused campaigns + "New campaign") then proceeds to `enroll-dialog`. After a successful enroll: snackbar "N leads added" with a **View** action → `/campaigns/:id?tab=leads`; selection cleared.

**`settings-tab`** — name, optional From name, daily cap (1–500, hint "New domains should start low — the workspace-wide warm-up cap also applies"), send window (two `<input type="time">`), days (`mat-button-toggle-group` multiple, Mon–Sun), time zone: shows the workspace zone and an optional per-campaign override `mat-select` (`Intl.supportedValuesOf('timeZone')`), plus a **Change workspace time zone** link → small dialog → `PUT /api/v1/workspaces/current/time-zone`. Saved via `PUT /campaigns/{id}`; `windowSummary` previews the result. Disabled while `Completed`.

### Misc
- `core/layout/shell.html` nav item **Campaigns** already exists (F12); nothing to change beyond the route.
- Accessibility basics for this feature: drag handle has an accessible name and **Move up/Move down** buttons on each card as the keyboard alternative to drag; status chips carry text; dialogs trap focus (Material default); form fields have labels.
- Copy rule: never claim opens are exact (F28 caveat) — nothing in this feature shows open rates.

## Tests (targeted, per parent §12)
- **`schedule-display.spec.ts`** *(required by the parent plan)* — `buildTimeline` for `[0,3,4]` → Day 0, 3, 7; unsorted input is sorted by `order`; single step; empty list → "No steps yet"; first step's delay is ignored (always Day 0); `windowSummary` for weekdays/all-days/odd sets, e.g. Tue+Thu only → "Tue, Thu", Mon–Fri collapses to "Mon–Fri".
- **`token-insert.spec.ts`** — insert at caret, replace a selection, insert at start/end, caret position afterwards; `unknownTokens` finds `{{foo}}`, ignores `{{firstName|there}}`, case-insensitive known names.
- **`step-editor.spec.ts`** — autosave debounces and sends `PUT` once; unknown token shows a warning; first step delay read-only; locked step has delete/drag disabled; preview shows `missingTokens`; AI generate fills the fields but doesn't save until blur; `ai_budget_exceeded` message.
- **`steps-tab.spec.ts`** — drag to a new index calls `reorderSteps` with the new id order; a drop involving a locked index is rejected with the snackbar and **no request**; server `409` reloads the list.
- **`enroll-dialog.spec.ts`** — dry-run on open, numbers rendered, **Confirm disabled when nothing would be added**, confirm sends `dryRun:false`, warnings shown for unverified/risky.
- **`campaign-detail.spec.ts`** — activate with `409 problems` opens the problems dialog and **Fix** switches to the Steps tab; `domain_unverified` opens the sender help; AI estimate shown only when `aiSteps > 0`; pause/activate button states per status.
- **`campaigns.service.spec.ts`** — paths/payloads; `enroll` sends exactly one of `leadIds`/`filter`.
- Backend: the four small additions above (`StepsStarted`, `sender`, `activation-preview`, `Sent`) — DTO mapping tests, cross-workspace 404 on `activation-preview`, estimate arithmetic with a fake `AiPricing`.

## Not in this feature
Campaign cloning/templates library, A/B variants, per-lead enrollment removal, rich-text/HTML editor, scheduled activation, analytics views (F28), deleting campaigns.

## Verification
`ng test`, `ng build`, `dotnet test`. Manually against a local API with `Email:DomainVerified=false`: create a campaign → add three steps (delays 0/3/4 → header shows "Day 0 → Day 3 → Day 7"), insert tokens by clicking chips with the caret mid-text, preview with a lead that has no company (warning appears), generate a draft with AI, reorder by drag **and** with the arrow buttons, **Activate** → problems dialog (domain unverified) → set the flag, activate again → AI estimate shown → confirm → status Active. From **Leads**, select two pages of leads → **Add to campaign** → preview shows skipped/warning counts → confirm → they appear in the Leads tab with "Step 1 of 3" and a next-send time in the workspace zone. Pause → next-send shows "Paused". Confirm that after step 1 has gone out (F23) the first card is locked.
