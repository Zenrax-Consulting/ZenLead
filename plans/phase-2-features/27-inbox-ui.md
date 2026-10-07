# Feature 27 — Inbox API & UI

**Branch:** `feature/inbox-ui`
**Sprint:** 4 (the UI can start as soon as F25's entities exist; classification chips light up when F26 lands)
**Depends on:** F25 (`InboxThread`/`InboxMessage`, `ReplyAddress`), F26 (`ClassificationEffects`, `NeedsReview`), F18 (`IEmailSender`), F23 (`ISuppressionList`), F13/F12 (shell, lazy routes).

## Goal
Read replies, answer them, and correct the classifier — from one screen. List of threads with filters, conversation view, reply composer that threads properly, manual reclassification, unread tracking with a nav badge, and ~30 s polling for new mail (SignalR is out of scope).

## Design decisions
- **Replying is outbound mail to a real person**, so it goes through the same safety rules as campaigns: suppressed/`Unsubscribed`/`Bounced` leads can't be replied to (`409`, UI explains), the message is **inserted before it is sent** (idempotent on a client-generated request id — a double click can't send twice), and a failed send is recorded as `Failed` and shown, never swallowed.
- **Threading headers**: `Reply-To` is the same `r-<enrollmentId>@reply.leads…` address so the lead's next answer lands in the same thread; `In-Reply-To` = the latest inbound `Message-ID`, `References` = the chain of known ids, subject `Re: <thread subject>` (not doubled).
- **Reading marks as read**, as the parent plan specifies — a side effect on `GET` is acceptable because the endpoint is authenticated (no link-prefetch risk); it is idempotent, and there is an explicit "mark unread".
- **Text only.** Message bodies are rendered with text interpolation and `white-space: pre-wrap`. Never `innerHTML`, never the stored sanitised HTML (it exists for a later feature).
- **No parent-plan proxy edit needed**: parent PBI 27.3 asks to add `/api/v1/inbox` and `/api/v1/webhooks` to `proxy.conf.js`, but the existing `"/api"` context already covers them — leave the file alone (webhooks are called by SendGrid against the tunnel, not through `ng serve`).

## Files to add/modify

### Domain / Infrastructure

**`Entities/InboxMessage.cs`** (F25, modified) — for **outbound manual replies**:
```csharp
public Guid? ClientRequestId { get; set; }                    // idempotency key from the browser
public OutboundDeliveryStatus? DeliveryStatus { get; set; }   // null for inbound & campaign sends; Pending | Sent | Failed for manual replies
public string? DeliveryError { get; set; }                    // ≤ 500
public Guid? SentByUserId { get; set; }
```
(`OutboundDeliveryStatus` enum in Domain.) Unique index `(ThreadId, ClientRequestId)` filtered `ClientRequestId IS NOT NULL`. Migration **`AddInboxReplies`**.

### Application

**`Dtos/Inbox/InboxDtos.cs`** (new)
```csharp
public record ThreadListQuery(int Page = 1, int PageSize = 25, ReplyClassification? Classification = null, bool? Unread = null,
                              bool? NeedsReview = null, Guid? CampaignId = null, string? Q = null);

public record ThreadListItem(Guid Id, Guid LeadId, string LeadName, string LeadEmail, string? CompanyName,
    Guid? CampaignId, string? CampaignName, string Subject, string Snippet, DateTime LastMessageAt,
    bool IsRead, ReplyClassification Classification, bool NeedsReview, int MessageCount);

public record MessageDto(Guid Id, MessageDirection Direction, string FromAddress, string? FromName, string Subject, string TextBody,
    string? FullTextBody,                                // only when it differs from TextBody (quoted history was stripped)
    DateTime OccurredAt, ReplyClassification Classification, decimal? ClassificationConfidence, ClassificationSource ClassifiedBy,
    bool NeedsReview, bool SenderMismatch, bool IsAutoReply, OutboundDeliveryStatus? DeliveryStatus, string? DeliveryError);

public record ThreadDetail(Guid Id, Guid LeadId, string LeadName, string LeadEmail, string? LeadTitle, string? CompanyName, LeadStatus LeadStatus,
    Guid? CampaignId, string? CampaignName, string Subject, ReplyClassification Classification, bool CanReply, string? CannotReplyReason,
    IReadOnlyList<MessageDto> Messages);

public record ReplyRequest(Guid ClientRequestId, string Body);
public record ReclassifyRequest(ReplyClassification Classification);
public record UnreadCountResponse(int Count);
```
**Validators:** `ThreadListQueryValidator` (bounds as F13), `ReplyRequestValidator` (`ClientRequestId` non-empty; body 1–10,000 chars after trim; no NUL), `ReclassifyRequestValidator` (`Interested | NotInterested | Unsubscribe | OutOfOffice` only — a human can't set `Unclassified` or `Bounce`).

**`Abstractions/IInboxRepository.cs`** (new; EF impl, all queries workspace-scoped via the tenant filter + explicit `WorkspaceId` check) — `SearchThreadsAsync(workspaceId, query)` (one query projecting the list item incl. last-message snippet and count; no N+1), `GetThreadAsync(workspaceId, id)` (with messages, ordered by `OccurredAt`), `MarkReadAsync(id, isRead)`, `UnreadCountAsync(workspaceId)`, `FindLatestInboundMessageIdAsync(threadId)`, `GetReferencesAsync(threadId)` (last ≤ 10 Message-IDs, oldest first), `TryAddOutboundPendingAsync(message)` (false on `(ThreadId, ClientRequestId)` violation), `FindByClientRequestAsync`, `SaveAsync`.

**`UseCases/Inbox/SendInboxReplyUseCase.cs`** (new)
```csharp
public async Task<ReplyOutcome> ExecuteAsync(Guid workspaceId, Guid userId, Guid threadId, ReplyRequest request, CancellationToken ct)
{
    var thread = await inbox.GetThreadForReplyAsync(workspaceId, threadId, ct);          // thread + lead + enrollment
    if (thread is null) return ReplyOutcome.NotFound;

    if (await inbox.FindByClientRequestAsync(threadId, request.ClientRequestId, ct) is { } already)
        return ReplyOutcome.Duplicate(already);                                          // double click / retry: return what was recorded

    var lead = thread.Lead;
    if (LeadStatusRules.IsSuppressed(lead.Status) || await suppression.ContainsAsync(workspaceId, lead.Email, ct))
        return ReplyOutcome.Blocked("This contact has unsubscribed or bounced, so you can't email them.");

    var subject = thread.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? thread.Subject : $"Re: {thread.Subject}";
    var now = clock.GetUtcNow().UtcDateTime;
    var message = new InboxMessage
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, ThreadId = threadId, Direction = MessageDirection.Outbound,
        FromAddress = email.Value.FromAddress, ToAddress = lead.Email, Subject = subject, TextBody = request.Body.Trim(),
        OccurredAt = now, ClientRequestId = request.ClientRequestId, DeliveryStatus = OutboundDeliveryStatus.Pending, SentByUserId = userId
    };
    if (!await inbox.TryAddOutboundPendingAsync(message, ct))                             // lost the double-click race: someone else owns the send
        return ReplyOutcome.Duplicate(await inbox.FindByClientRequestAsync(threadId, request.ClientRequestId, ct));

    var lastInbound = await inbox.FindLatestInboundMessageIdAsync(threadId, ct);
    var refs = await inbox.GetReferencesAsync(threadId, ct);
    var headers = new Dictionary<string, string>();
    if (lastInbound is not null) headers["In-Reply-To"] = $"<{lastInbound}>";
    if (refs.Count > 0) headers["References"] = string.Join(' ', refs.Select(r => $"<{r}>"));

    var result = await sender.SendAsync(new OutboundEmail(
        new EmailAddress(lead.Email, lead.Name), subject, message.TextBody, HtmlBody: null,
        ReplyTo: thread.EnrollmentId is { } e ? new EmailAddress(ReplyAddress.Encode(e, email.Value.InboundDomain)) : null,
        Headers: headers, CustomArgs: new Dictionary<string, string> { ["inboxMessageId"] = message.Id.ToString("N"), ["workspaceId"] = workspaceId.ToString("N") },
        Categories: ["inbox-reply"], TrackOpens: false), ct);

    message.DeliveryStatus = result.Success ? OutboundDeliveryStatus.Sent : OutboundDeliveryStatus.Failed;
    message.DeliveryError = result.Success ? null : Truncate(result.Error, 500);
    if (result.Success) { thread.Thread.LastMessageAt = now; thread.Thread.IsRead = true; }
    await inbox.SaveAsync(ct);
    return result.Success ? ReplyOutcome.Sent(message) : ReplyOutcome.Failed(message);
}
```
`Reply-To` reuses the enrollment-encoded address; thread has no `TrackOpens` and **no unsubscribe footer** — this is a one-to-one answer to someone who wrote to us (the suppression check above is the compliance guard). Retrying after `Failed` uses a **new** `ClientRequestId` (the UI generates one per attempt), so a failed row never blocks a retry.

**`UseCases/Inbox/ReclassifyThreadUseCase.cs`** — finds the thread's **latest inbound non-auto message** (`NotFound`/`NothingToClassify` otherwise), calls `ClassificationEffects.ApplyAsync(workspaceId, messageId, label, 1m, manual: true, note: null, ct)` (F26), returns the refreshed `ThreadListItem`. An `Unsubscribe` label by a human takes the same sticky actions as the AI path (suppression, stop other enrollments).

**`UseCases/Inbox/SuggestReplyUseCase.cs`** *(stretch, behind `features.aiReplySuggest`)* — builds an `EmailComposeContext` from the thread (lead + the latest inbound text as `AdditionalContext`, framed as "draft a short reply to this message"), checks `IAiBudget`, records usage with `Purpose = Compose`, returns `{ body }` only. Never sends.

### Api

**`Controllers/V1/InboxController.cs`** (new, `[Authorize]`, `api/v1/inbox`; every action starts with the workspace-claim check; another workspace's thread is `404`)
| Route | Behaviour |
|---|---|
| `GET threads` | `PagedResult<ThreadListItem>`, newest first; filters: `classification`, `unread`, `needsReview`, `campaignId`, `q` (lead name/email/subject, LIKE-escaped as in F13) |
| `GET threads/{id}` | `ThreadDetail`; **marks the thread read** (single `UPDATE`); `CanReply = false` + `CannotReplyReason` when the lead is suppressed |
| `POST threads/{id}/read` | `{ isRead }` — explicit mark read/unread |
| `POST threads/{id}/reply` | `SendInboxReplyUseCase`: `201` + `MessageDto` (`Sent`), `200` for `Duplicate`, `409 { code: "suppressed" }`, `502 { code: "send_failed", message }` (the `Failed` row exists and is returned in the next `GET`), `404`, `400` validation; `[EnableRateLimiting("reply")]` — new policy, 30/min per workspace |
| `POST threads/{id}/classification` | `ReclassifyThreadUseCase` |
| `GET unread-count` | `{ count }` — cheap, indexed `(WorkspaceId, IsRead)`; the shell polls it |
| `POST threads/{id}/suggest-reply` | *(stretch)* |

Register the repository and use cases in `Program.cs`; add `ReplyPolicy` to `RateLimiting.cs`.

### Angular (`features/inbox/`, lazy `InboxModule`, route `/inbox` replacing the F12 stub)

**`inbox.models.ts`, `inbox.service.ts`** — typed mirrors of the DTOs; `threads(query)`, `thread(id)`, `setRead(id, isRead)`, `reply(id, { clientRequestId, body })`, `reclassify(id, classification)`, `unreadCount()`, `suggestReply(id)`.

**`inbox-unread.service.ts`** (root; used by the shell) — `count$ = timer(0, 60_000).pipe(switchMap(() => service.unreadCount()), catchError(() => EMPTY), shareReplay(1))`, **paused while the tab is hidden** (`visibilityState` via `fromEvent(document, 'visibilitychange')`); `refreshNow()` after reading/replying. **Shell** (F12/F24 file) nav item *Inbox* shows `matBadge` with the count (hidden at 0), with an `aria-label` "Inbox, N unread".

**`inbox-page`** — master/detail. Wide: list (≈ 380 px) + conversation; narrow (< 800 px): list *or* conversation with a back button. Route `/inbox` and `/inbox/:threadId` (selection lives in the URL; filters in query params, like F13).
- **Filters:** chips — *All, Unread, Needs review, Interested, Not interested, Unsubscribe, Out of office, Unclassified* (single-select) + a campaign `mat-select` + lead search box (debounced 300 ms). Active filter shown with text, not colour alone.
- **Thread rows:** lead name (bold when unread) + company, subject, 1-line snippet, relative time (`Intl.RelativeTimeFormat`), **classification chip** (icon + label, tone per class; "Needs review" amber chip when flagged), campaign name. Selected row highlighted; `aria-current`.
- **Polling:** `timer(30_000, 30_000)` re-queries the current filter/page while the tab is visible; the selected thread and scroll position are preserved (merge by id, don't re-render the whole list); a small "N new" toast when the top of the list changed while the user was scrolled down. All async state changes call `cdr.markForCheck()` (zoneless).

**`conversation`** (input: thread id)
- Header: lead name (link → `/leads/:id`), email, company/title, lead status chip, campaign link, **classification menu** (`mat-menu`: Interested / Not interested / Unsubscribe / Out of office, current one checked) → `reclassify`; confirming **Unsubscribe** shows a dialog ("This stops all emails to <email> and can't be undone from here").
- **`NeedsReview` banner** when the latest inbound message has it: "The AI wasn't sure about this reply (it thinks: *Unsubscribe*, 62%). Please confirm" with one-click **Confirm** (sets the suggested label manually) and **Choose…**.
- Messages: stacked cards, inbound vs outbound visually and textually distinct ("You" / lead name), timestamp, chips: *Auto-reply*, *Sent from a different address than the lead* (`senderMismatch`), delivery state for manual replies (*Sending…* / *Failed — retry* with the error). **Body is rendered with text interpolation inside `white-space: pre-wrap`** — no `[innerHTML]` anywhere in this feature. When `fullTextBody` is present: **Show quoted text** toggle.
- Opening a thread calls `GET threads/{id}` (marks read) then `inbox-unread.refreshNow()`.
- **Composer** (bottom): textarea (autosize, 10,000 max with counter), subject shown read-only as `Re: …`, **Send** (primary) disabled while empty/sending or `!canReply` (then replaced by the `cannotReplyReason` text), optional **Suggest reply** (flag `features.aiReplySuggest`, shows budget message on `ai_budget_exceeded`). Each send attempt generates `crypto.randomUUID()` as `clientRequestId` (kept until the request settles so a network retry of the *same* attempt is idempotent); success appends the message and clears the box; `502 send_failed` keeps the text, shows the error, and offers retry (new id).
- Empty states: no threads at all ("Replies to your campaigns will appear here"), no matches for the filters (Clear filters), no thread selected.

**`app-routing-module.ts`**: `inbox` → `loadChildren` (replaces `ComingSoon`). `environments`: `features.inbox = true`, `features.aiReplySuggest = false`.

## Tests (priority)
**Backend** (`TestDb`, `FakeEmailSender`, `FakeTimeProvider`; `ApiFactory` for HTTP-level):
- **List/filter:** paging totals, newest-first, each filter (classification, unread, needsReview, campaign, `q` incl. `%`/`_` literals), snippet is the *latest* message, `MessageCount`, single-query shape (no per-row queries — interceptor count).
- **Tenant isolation (priority):** workspace B gets **404** for A's thread on `GET`, `read`, `reply`, `classification`, `suggest-reply`; B's list never contains A's threads; unread-count is per workspace; a `ReplyRequest` can't target a thread by passing A's lead id.
- **Read semantics:** `GET threads/{id}` flips `IsRead` once and is idempotent; a new inbound message makes it unread again; explicit mark-unread works; unread-count tracks it.
- **Reply:** success → outbound `InboxMessage` (`DeliveryStatus = Sent`, `SentByUserId`), thread `LastMessageAt` updated, sender received `In-Reply-To` = latest inbound id, `References` chain (≤ 10, oldest first), subject `Re:` not doubled (`Re: Re:` guard), `Reply-To` decodes to the enrollment id, `TrackOpens` false, no unsubscribe footer; same `ClientRequestId` twice → **one** send and the second call returns the first message (also under `Task.WhenAll` on SQLite); sender failure → row `Failed` with the error, **502**, and a retry with a new id succeeds; suppressed/`Unsubscribed`/`Bounced` lead (status *or* suppression row) → `409 suppressed`, nothing sent, no row; validation (empty/too long body); rate limit.
- **Reclassify:** each allowed label applies via `ClassificationEffects` (`manual: true`): `Unsubscribe` → lead `Unsubscribed` + suppression + other enrollments stopped; `Interested` clears `NeedsReview`; `Unclassified`/`Bounce` rejected 400; thread with no inbound human message → clear 409/400.
- **Authorization inventory (F19/F29.3):** every `/api/v1/inbox/**` route requires the default (tenant) policy — the super admin gets 403.
- **Suggest reply (stretch):** budget `Exceeded` → no AI call; never sends.

**Angular:**
- `inbox-page.spec.ts` — filters map to query params and a service call; changing a filter resets paging; **polling merges without losing the selected thread**; polling stops when the document is hidden and resumes on visible (fake `visibilityState`); responsive back button.
- `conversation.spec.ts` — **a message body containing `<img src=x onerror=alert(1)>` and `<script>` renders as literal text** (assert `textContent` and absence of `img`/`script` elements); quoted-text toggle; chips for auto-reply/mismatch; `NeedsReview` banner Confirm sends `reclassify`; unsubscribe confirmation dialog; opening a thread triggers the read call and unread refresh.
- `reply-composer.spec.ts` — Send disabled when empty/sending/`!canReply` (shows the reason); one `clientRequestId` per attempt, a *new* one after a failure; 502 keeps the text; 409 shows the suppression message; success clears and appends.
- `inbox-unread.service.spec.ts` — polls every 60 s, pauses when hidden, `refreshNow` forces a fetch; shell badge hides at 0 and exposes an accessible label.
- `inbox.service.spec.ts` — paths/params/payloads.

## Not in this feature
SignalR/real-time push, attachments (inbound ignored, outbound unsupported), HTML rendering of replies, forwarding/CC/BCC, assigning threads to colleagues, snooze/archive/labels, bulk actions, a quarantine viewer, a templates library for replies.

## Verification
`dotnet ef migrations add AddInboxReplies …`, `dotnet test`, `ng test`, `ng build`. With F23+F25+F26 running locally: reply from a real mailbox → within ~1 minute the Inbox badge shows 1 and the thread appears with a classification chip; open it (badge clears), read the conversation including the original outbound message; **Show quoted text** reveals the stripped history; send an answer from the composer → it arrives in the mailbox *in the same thread* (check headers: `In-Reply-To`, `References`, subject `Re:`), and replying again from the mailbox lands in the same thread; click **Send** twice quickly → one email; change the label to *Unsubscribe* → lead becomes `Unsubscribed` and the composer is replaced by the explanation; reply "please remove me" from the mailbox → classified `Unsubscribe`, sequence paused/suppressed (F26). Paste `<script>alert(1)</script>` into a reply from the mailbox → shown as text. Resize to phone width → single-pane navigation works.
