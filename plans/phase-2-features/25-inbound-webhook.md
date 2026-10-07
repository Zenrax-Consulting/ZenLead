# Feature 25 — Inbound Parse Webhook & Threading

**Branch:** `feature/inbound-webhook`
**Sprint:** 4
**Depends on:** F17 (**live MX for `reply.leads…` and the Inbound Parse host**), F23 (`ReplyAddress`, `EmailMessage`, the sender), F22 (`EnrollmentRules.ApplyStop`), F11 (`ForWorkspace`, `LeadStatusRules`), the persistent dev tunnel (parent §1c).

## Goal
A reply to a campaign email lands in ZenLead within about a minute, is attached to the right lead/thread, stops the sequence, and is never silently dropped. Also: outbound campaign sends create/attach to a thread so the inbox shows the whole conversation. Classification is F26; the inbox UI is F27.

## Design decisions
- **Always answer 200 once the request is authentic.** SendGrid retries non-2xx for days. Unmatched, duplicate, oversized-attachment or auto-reply mail is *handled* (quarantined, deduped, flagged) and acknowledged; only a bad secret gets `401`.
- **Three-level matching, strongest first:** (1) the `Reply-To` token — recipient address `r-<enrollmentId>@reply.leads…` (F23 `ReplyAddress`); (2) `In-Reply-To`/`References` headers against our sent messages; (3) the sender's email against leads that we have recently emailed. Anything else → **quarantine table + warning log**.
- **A replier who isn't the lead is still a reply** (assistants, forwards, aliases) but is flagged `SenderMismatch`. If the sender differs **and** SendGrid's `dkim`/`SPF` verdicts both aren't `pass`, it is quarantined instead of attached — that blocks someone who learned an enrollment id from injecting fake "interested/unsubscribe" replies.
- **Store text, escape on render.** Plain text is the primary body (quoted history stripped, full text kept separately); sanitised HTML is stored for later but F27 renders text only.
- **SendGrid's own `Message-ID`.** SendGrid rewrites the `Message-ID` of outgoing mail, so header-based matching (level 2) only works if we know that value. We record SendGrid's `X-Message-Id` at send time and (optionally, F28) the real `smtp-id` from events. **Verify with a real round-trip in this feature** (see Verification) and adjust level 2 accordingly — level 1 is the dependable path.
- **Parsing mode.** The parent plan's mode is SendGrid's default *parsed multipart fields*. That mode hands us text already decoded as UTF-8 by ASP.NET, which garbles replies from senders using other charsets (SendGrid reports them in a `charsets` field). The parser sits behind `IInboundEmailParser`; if real-world captures show garbled text, switch the Inbound Parse host to **"POST the raw, full MIME message"** and add a `MimeKit`-based implementation — nothing else changes. Decide after the first few real replies.

## Files to add/modify

### Domain

**`Enums/`** (new): `MessageDirection { Inbound, Outbound }`, `ReplyClassification { Unclassified, Interested, NotInterested, Unsubscribe, OutOfOffice, Bounce }` (F26 adds the classifier; this feature only assigns `OutOfOffice`/`Bounce` from headers).

**`Entities/InboxThread.cs`**, **`InboxMessage.cs`**, **`InboundQuarantine.cs`** (new)
```csharp
public class InboxThread : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid LeadId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? EnrollmentId { get; set; }                     // one thread per enrollment (unique)
    public string Subject { get; set; } = string.Empty;         // the first outbound subject; replies keep it
    public DateTime LastMessageAt { get; set; }
    public bool IsRead { get; set; }                            // false while there is an unread inbound message
    public ReplyClassification Classification { get; set; }    // denormalised: latest inbound classification (list filter/chips)
    public DateTime CreatedAt { get; set; }
}

public class InboxMessage : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ThreadId { get; set; }
    public MessageDirection Direction { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string ToAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string TextBody { get; set; } = string.Empty;        // quoted history stripped (inbound)
    public string? FullTextBody { get; set; }                   // as received, for when stripping is wrong
    public string? HtmlBody { get; set; }                       // sanitised; not rendered in Phase 2
    public DateTime OccurredAt { get; set; }                    // received / sent
    public ReplyClassification Classification { get; set; }
    public decimal? ClassificationConfidence { get; set; }
    public string? RawMessageId { get; set; }                   // inbound Message-ID (dedupe key); outbound: SmtpMessageId when known
    public Guid? EmailMessageId { get; set; }                   // outbound: the campaign EmailMessage
    public bool SenderMismatch { get; set; }
    public bool IsAutoReply { get; set; }
    public string? AuthSummary { get; set; }                    // e.g. "dkim=pass; spf=pass"
}

/// <summary>Not tenant-scoped on purpose: by definition we couldn't determine the workspace.</summary>
public class InboundQuarantine
{
    public Guid Id { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string Reason { get; set; } = string.Empty;          // no_match | ambiguous | spoof_suspected | too_large | parse_error
    public string? FromAddress { get; set; }
    public string? ToAddress { get; set; }
    public string? Subject { get; set; }
    public string? HeadersSnippet { get; set; }                 // first 4 KB of headers, no body (reply content is personal data)
}
```
EF: `InboxThread` unique `(EnrollmentId)` filtered not null, indexes `(WorkspaceId, LastMessageAt)`, `(WorkspaceId, IsRead)`, `(WorkspaceId, Classification)`, FK Lead **Restrict**; `InboxMessage` index `(ThreadId, OccurredAt)`, **unique `(WorkspaceId, RawMessageId)` filtered `RawMessageId IS NOT NULL AND Direction = 0`** (inbound dedupe), `Subject` 500, `FromAddress`/`ToAddress` 256, `AuthSummary` 100; `InboundQuarantine` index `(ReceivedAt)` (a 30-day purge is a Hangfire housekeeping line in F29). Migration **`AddInbox`**.

**`Inbound/ReplyTextCleaner.cs`** (new, pure) — best-effort quoted-text removal
```csharp
public static class ReplyTextCleaner
{
    private static readonly Regex[] Cutoffs =
    [
        new(@"^\s*On .{5,200}wrote:\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase),          // Gmail/Apple: "On Tue, 5 Oct 2026 at 10:15, X <x@y> wrote:"
        new(@"^\s*-{2,}\s*Original Message\s*-{2,}", RegexOptions.Multiline | RegexOptions.IgnoreCase), // Outlook
        new(@"^\s*From:\s.+\r?\n\s*Sent:\s", RegexOptions.Multiline | RegexOptions.IgnoreCase),         // Outlook header block
        new(@"^\s*_{5,}\s*$", RegexOptions.Multiline),
        new(@"^\s*Le .{5,200}a écrit\s*:\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)       // FR; add locales when seen in real mail
    ];

    public static string StripQuoted(string text)
    {
        var cut = Cutoffs.Select(r => r.Match(text)).Where(m => m.Success).Select(m => m.Index).DefaultIfEmpty(text.Length).Min();
        var head = text[..cut];
        var lines = head.Split('\n').Where(l => !l.TrimStart().StartsWith('>'));                      // drop inline-quoted lines
        var cleaned = string.Join('\n', lines).Trim();
        return cleaned.Length == 0 ? text.Trim() : cleaned;                                            // never return an empty reply because the heuristic over-matched
    }
}
```
**`Inbound/AutoReplyDetector.cs`** (new, pure; header dictionary + from + subject → `AutoReplyKind { None, OutOfOffice, Bounce }`)
- `Auto-Submitted` present and not `no`, `X-Autoreply`/`X-Autorespond` present, `Precedence: auto_reply|bulk|junk`, `X-Auto-Response-Suppress` present → `OutOfOffice`;
- `Content-Type` contains `multipart/report` with `delivery-status`, `Return-Path: <>`, from local-part `mailer-daemon`/`postmaster` → `Bounce`;
- subject prefixes `Automatic reply:`, `Out of Office`, `Autoreply:`, `Auto:` (case-insensitive) → `OutOfOffice` (lowest-confidence signal; only used if no header matched).

### Application

**`Abstractions/IInboundEmailParser.cs`** (new)
```csharp
public record InboundEmail(
    string FromAddress, string? FromName, IReadOnlyList<string> ToAddresses, string Subject,
    string? Text, string? Html, string? MessageId, string? InReplyTo, IReadOnlyList<string> References,
    IReadOnlyDictionary<string, string> Headers, string? Dkim, string? Spf, DateTime ReceivedAt);

public interface IInboundEmailParser
{
    /// <summary>Throws <see cref="InboundParseException"/> for input that is not an inbound-parse post at all.</summary>
    InboundEmail Parse(IReadOnlyDictionary<string, string> fields, DateTime receivedAtUtc);
}
```
> Application doesn't reference ASP.NET, so the parser takes the form's *text fields* flattened into a dictionary by the controller. That keeps it framework-free and testable from plain dictionaries (and from captured JSON fixtures); attachments are never passed in.

**`Abstractions/IHtmlSanitizer.cs`** → `string Sanitize(string html)`; Infrastructure impl `GanssHtmlSanitizer` (package `HtmlSanitizer`) with an **empty-ish allow-list**: `p, br, b, strong, i, em, u, ul, ol, li, blockquote, a` (href `http/https/mailto` only, `rel="nofollow noopener"`, no `target`), no images/styles/classes/forms/iframes/scripts, comments removed.

**`Abstractions/IInboundStore.cs`** (new) — cross-tenant lookups are explicit and by unguessable id; everything after the workspace is known is scoped with `ForWorkspace`
```csharp
public record EnrollmentRef(Guid EnrollmentId, Guid WorkspaceId, Guid CampaignId, Guid LeadId, string LeadEmail);

public interface IInboundStore
{
    Task<EnrollmentRef?> FindEnrollmentByIdAsync(Guid enrollmentId, CancellationToken ct);                                  // level 1 (IgnoreQueryFilters, PK lookup)
    Task<EnrollmentRef?> FindByMessageReferencesAsync(IReadOnlyCollection<string> messageIds, CancellationToken ct);        // level 2
    Task<IReadOnlyList<EnrollmentRef>> FindRecentByLeadEmailAsync(string email, DateTime sentSinceUtc, CancellationToken ct); // level 3: latest outbound per workspace
    Task<InboxThread> GetOrCreateThreadAsync(EnrollmentRef target, string subject, DateTime now, CancellationToken ct);
    Task<bool> TryAddInboundAsync(InboxMessage message, CancellationToken ct);                                               // false on dedupe-index violation
    Task<InboundApplyResult> ApplyReplyEffectsAsync(EnrollmentRef target, bool isAutoReply, CancellationToken ct);          // lead → Replied, enrollment stop
    Task AddQuarantineAsync(InboundQuarantine row, CancellationToken ct);
}
```
**`UseCases/Inbound/ProcessInboundEmailUseCase.cs`** (new)
```csharp
public async Task<InboundResult> ExecuteAsync(InboundEmail mail, CancellationToken ct)
{
    var now = clock.GetUtcNow().UtcDateTime;
    var auto = AutoReplyDetector.Detect(mail.Headers, mail.FromAddress, mail.Subject);

    // 1) Resolve the target enrollment — strongest signal first
    EnrollmentRef? target = null; string via = "";
    foreach (var to in mail.ToAddresses)
        if (ReplyAddress.TryDecode(to, email.Value.InboundDomain, out var enrollmentId) && await store.FindEnrollmentByIdAsync(enrollmentId, ct) is { } byToken)
        { target = byToken; via = "reply-to-token"; break; }

    if (target is null && IdsFrom(mail) is { Count: > 0 } ids && await store.FindByMessageReferencesAsync(ids, ct) is { } byHeader)
    { target = byHeader; via = "headers"; }

    if (target is null)
    {
        var candidates = await store.FindRecentByLeadEmailAsync(mail.FromAddress, now.AddDays(-60), ct);
        if (candidates.Count == 1) { target = candidates[0]; via = "sender-email"; }
        else return await QuarantineAsync(mail, candidates.Count == 0 ? "no_match" : "ambiguous", ct);       // never guess between workspaces
    }

    // 2) Sender sanity
    var mismatch = !string.Equals(mail.FromAddress, target.LeadEmail, StringComparison.OrdinalIgnoreCase);
    if (mismatch && !(IsPass(mail.Dkim) || IsPass(mail.Spf)) && via != "sender-email")
        return await QuarantineAsync(mail, "spoof_suspected", ct);

    // 3) Persist (idempotent on Message-ID)
    var text = mail.Text ?? HtmlToText(mail.Html);
    var thread = await store.GetOrCreateThreadAsync(target, mail.Subject, now, ct);
    var message = new InboxMessage
    {
        Id = Guid.NewGuid(), WorkspaceId = target.WorkspaceId, ThreadId = thread.Id, Direction = MessageDirection.Inbound,
        FromAddress = mail.FromAddress, FromName = mail.FromName, ToAddress = mail.ToAddresses.FirstOrDefault() ?? "", Subject = Truncate(mail.Subject, 500),
        TextBody = ReplyTextCleaner.StripQuoted(text), FullTextBody = text, HtmlBody = mail.Html is null ? null : sanitizer.Sanitize(mail.Html),
        OccurredAt = mail.ReceivedAt, RawMessageId = mail.MessageId ?? SyntheticId(mail), SenderMismatch = mismatch,
        IsAutoReply = auto != AutoReplyKind.None,
        Classification = auto switch { AutoReplyKind.OutOfOffice => ReplyClassification.OutOfOffice, AutoReplyKind.Bounce => ReplyClassification.Bounce, _ => ReplyClassification.Unclassified },
        AuthSummary = $"dkim={mail.Dkim ?? "none"}; spf={mail.Spf ?? "none"}"
    };
    if (!await store.TryAddInboundAsync(message, ct)) return InboundResult.Duplicate;                          // SendGrid redelivery: acknowledged, no second effects

    // 4) Effects: only real human replies stop the sequence
    await store.ApplyReplyEffectsAsync(target, isAutoReply: message.IsAutoReply, ct);
    logger.LogInformation("Inbound reply attached via {Via} to enrollment {EnrollmentId}", via, target.EnrollmentId);   // ids only: no addresses/bodies in logs
    return InboundResult.Attached(thread.Id, message.Id);       // F26 hooks in here: enqueue classification for non-auto replies
}
```
`ApplyReplyEffectsAsync` (EF impl, `ForWorkspace`): for **non-auto** replies — `LeadStatusRules.CanTransition(lead.Status, Replied)` → set; `EnrollmentRules.ApplyStop(enrollment, Replied)`; thread `IsRead = false`, `LastMessageAt = now`. For **auto-replies** (`OutOfOffice`/`Bounce`): only update the thread timestamp/unread flag — the sequence continues (parent PBI 25.4). A reply that arrives for an enrollment that is already `Completed` still marks the lead `Replied` (the enrollment keeps its final status, per `ApplyStop`).
`IdsFrom(mail)` = `In-Reply-To` + `References` ids, plus, for each, its local part and the segment before the first `.` (to match SendGrid's `X-Message-Id`-based ids); `FindByMessageReferencesAsync` matches `EmailMessage.ProviderMessageId IN (…)` or `SmtpMessageId IN (…)`. `SyntheticId` = `"synthetic:" + SHA256(from|date|subject|text)` so mail without a Message-ID still dedupes.

**Outbound threading (modifies F23).** `ISenderStore` gains `void AttachOutbound(SendContext ctx, EmailMessage message)`; in `SendDueEmailsUseCase` step 6 (success branch) call it **before** `SaveAsync` so the thread, the outbound `InboxMessage`, the `EmailMessage` update and the enrollment advance commit atomically:
```csharp
// EF store: find-or-add the thread for ctx.Enrollment (Subject = step 1 subject when new), add InboxMessage { Direction = Outbound,
// FromAddress = configured sender, ToAddress = lead.Email, Subject/TextBody = what was sent, OccurredAt = message.SentAt, EmailMessageId = message.Id, IsAutoReply = false }
```
Existing F23 tests keep passing (the fake store ignores it); add one asserting the thread/message exist after a send.

### Infrastructure

- `SendGridFormParser : IInboundEmailParser` — maps fields `from`, `to`, `subject`, `text`, `html`, `headers`, `dkim`, `SPF`, `envelope`, `charsets`. `from` is `"Name <addr>"` → `System.Net.Mail.MailAddress` (tolerant fallback: regex the address; never throw on odd display names). `to` and `envelope.to` (JSON) are merged and deduped (the *envelope* has the recipient that actually hit our MX, which is what carries the token even when the visible `To:` differs). `headers` is unfolded (RFC 5322 continuation lines) into a case-insensitive dictionary; `Message-ID`/`In-Reply-To`/`References` split on whitespace and trimmed of `<>`. Missing `from` or no recipients → `InboundParseException`. Logs a one-line warning if `charsets` names a non-UTF-8 charset for `text` (the signal to switch to raw MIME).
- `GanssHtmlSanitizer`, `InboundStore` (EF), `AddInbox` migration, DbSets, configs.
- **Raw-capture switch (Development only):** `Webhooks:CaptureRaw=true` makes the controller write each request's flattened fields to `%TEMP%\zenlead-inbound\<timestamp>.json` — how real fixtures are collected for the tests below (strip personal data before committing).

### Api

**`Controllers/V1/Webhooks/SendGridInboundController.cs`** (new)
```csharp
[ApiController]
[AllowAnonymous]
[Route("api/v1/webhooks/sendgrid/inbound")]
[RequestSizeLimit(26_214_400)]                               // SendGrid caps messages at ~30 MB; we ignore attachments but must accept the post
[RequestFormLimits(MultipartBodyLengthLimit = 26_214_400, ValueLengthLimit = 4_194_304)]
public class SendGridInboundController(IInboundEmailParser parser, ProcessInboundEmailUseCase process, IOptions<WebhookOptions> options, ILogger<SendGridInboundController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive([FromQuery] string? secret, CancellationToken ct)
    {
        if (!SecretMatches(secret, options.Value.InboundSecret)) return Unauthorized();       // no body parsing before auth
        if (!Request.HasFormContentType) return BadRequest();

        var form = await Request.ReadFormAsync(ct);
        var fields = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());               // files (attachments) are never read
        try
        {
            var result = await process.ExecuteAsync(parser.Parse(fields, DateTime.UtcNow), ct);
            return Ok();                                                                       // attached / duplicate / quarantined: all acknowledged
        }
        catch (InboundParseException ex)
        {
            await process.QuarantineRawAsync("parse_error", fields, ct);                       // keep the evidence, then ack so SendGrid stops retrying
            logger.LogWarning("Unparseable inbound post: {Reason}", ex.Message);
            return Ok();
        }
    }

    private static bool SecretMatches(string? provided, string? expected)
        => !string.IsNullOrEmpty(expected) && provided is not null
           && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
```
`WebhookOptions.InboundSecret` from `SendGrid:InboundSecret` (user-secrets; ≥ 32 random chars; **required when `Email:Provider=SendGrid`** in `StartupConfiguration`). Add the route to the F19 anonymous allow-list. **Logging:** the secret rides in the query string — F29's request-logging redaction must cover `secret`; until then do not enable request-URL logging in the tunnel environment. Nothing to add to `proxy.conf.js` (`/api/...`); SendGrid calls the tunnel/API directly, not `ng serve`.
Set the real Inbound Parse URL in the SendGrid UI to `https://<tunnel>/api/v1/webhooks/sendgrid/inbound?secret=<InboundSecret>` (F17 placeholder replaced).

## Tests (priority)
**Fixtures:** `ZenLead.Tests/Fixtures/Inbound/*.json` — field dictionaries captured with `CaptureRaw` from real replies (Gmail, Outlook/O365, Apple Mail, an OOO auto-reply, a DSN bounce, a non-UTF-8 sender if one can be provoked) with personal data replaced; plus hand-written synthetic ones for edge cases.
- **`SendGridFormParserTests`** — each fixture parses: from name/address (quoted names, commas in names, missing name), envelope recipients merged with `to`, header unfolding, `References` split, Message-ID without `<>`; missing `from` → `InboundParseException`; non-UTF-8 charset logs the warning.
- **`ReplyTextCleanerTests`** — Gmail "On … wrote:", Outlook "-----Original Message-----" and "From:/Sent:" blocks, `>`-quoted lines, top-posting with signature, a reply that *is only* a quote (returns the original, never empty), very long input stays linear-time (no catastrophic regex).
- **`AutoReplyDetectorTests`** — `Auto-Submitted: auto-replied`, `Auto-Submitted: no` (not auto), `X-Autoreply`, `Precedence: bulk`, DSN `multipart/report`, `mailer-daemon` sender, subject-only OOO; a normal reply with "out of office" *inside the body* is **not** auto.
- **`ProcessInboundEmailUseCaseTests`** (fake `IInboundStore`, fake sanitizer):
  - **Matching:** token in `to` → attached; token only in the envelope list → attached; token wrong/malformed → falls to headers; `In-Reply-To` matching `ProviderMessageId`-prefixed id → attached; sender-email fallback with exactly one recent candidate → attached; two workspaces both emailed this address → **quarantined `ambiguous`** (never guessed); no candidate → `no_match` quarantine with headers snippet, **no body stored**.
  - **Spoof/unknown:** sender ≠ lead email with `dkim=none`/`spf=fail` → `spoof_suspected` quarantine; same mismatch with `dkim=pass` → attached with `SenderMismatch = true`; token for an enrollment that doesn't exist → not attached.
  - **Idempotency:** the same Message-ID delivered twice → one `InboxMessage`, one effect (second returns `Duplicate`, lead/enrollment not touched again); no Message-ID → synthetic id still dedupes; two *different* replies in one thread both stored.
  - **Effects:** human reply → lead `Replied`, enrollment `Replied`, `NextSendAt = null`, thread unread; **OOO auto-reply → enrollment still `Active`, lead status unchanged**, message flagged and classified `OutOfOffice`; DSN → `Bounce`, no stop; reply to a `Completed` enrollment → lead `Replied`, enrollment stays `Completed`; reply from a lead already `Unsubscribed` → stored, no status regression (`CanTransition` false).
  - **Cross-workspace (priority):** a token whose enrollment belongs to workspace A can only ever write into A (assert `WorkspaceId` on thread/message/effects); in a two-workspace SQLite test, an inbound for A never changes B's lead with the same email.
  - Logs contain ids but **no addresses or body text** (captured `ILogger` assertion).
- **`GanssHtmlSanitizerTests`** — `<script>`, `onerror=`, `javascript:` hrefs, `<img src>` tracking pixels, `<iframe>`, `<style>`, `data:` URLs, malformed nesting → stripped; plain formatting and `https://` links survive with `rel="nofollow noopener"`.
- **`SendGridInboundControllerTests`** (`ApiFactory`) — missing/wrong secret → 401 and the use case is **not** invoked; correct secret + multipart fixture → 200 and rows written; non-multipart → 400; parse error → 200 + quarantine row; oversized attachment present but ignored; the route is on the anonymous allow-list and nothing else became anonymous.
- **Outbound threading** (extend F23 sender tests) — after a send: one thread per enrollment (second step reuses it), outbound `InboxMessage` with `EmailMessageId`, all committed in the same save as the enrollment advance (fake store failure rolls back neither half separately).
- `ReplyAddress` round-trip tests from F23 remain the contract; add a test that the address the sender builds is what `TryDecode` accepts for the configured inbound domain.

## Not in this feature
Classification and the unsubscribe-by-reply action (F26), inbox API/UI (F27), attachments (ignored), reading bounces from Return-Path (F28's Event Webhook owns bounces), a quarantine UI, a "re-attach quarantined mail" tool, raw-MIME parsing unless the charset check says it's needed.

## Verification
1. Tunnel up; Inbound Parse URL set with the secret; `Webhooks:CaptureRaw=true`. Send a campaign step-1 email (F23) to a mailbox you control.
2. **Reply** from that mailbox → within ~1 minute the API log shows "Inbound reply attached via reply-to-token"; DB shows an `InboxThread` (with the outbound message from the send and your inbound reply), lead `Replied`, enrollment `Replied`. Capture file appears in `%TEMP%\zenlead-inbound`.
3. **Level-2 check:** forward-reply from a different address *with* the original quoted? Not needed — instead inspect the received email's `Message-ID` header and the stored `ProviderMessageId` and record whether they correlate (note the finding at the top of this doc and in `docs/sendgrid-dns-status.md`).
4. Reply again (same thread) → second inbound message on the same thread; redeliver the capture manually with `curl` → no duplicate row. Send an auto-reply (set OOO on the mailbox) → classified `OutOfOffice`, sequence continues. Email `r-<guid>@reply.leads…` from an unrelated address with a random guid → quarantine row, 200.
5. Wrong secret → 401. `dotnet test`.
