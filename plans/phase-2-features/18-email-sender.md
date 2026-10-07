# Feature 18 — Transactional Email Sender

**Branch:** `feature/email-sender`
**Sprint:** 2
**Depends on:** F17 (API key, verified domain — or the single-sender fallback). F14.0 for `OpsController`.

## Goal
`IEmailSender` in Application with a SendGrid implementation and a dev "log to console" fake, `Workspace.TimeZone` (needed by F22's send windows), and a way to prove a real email goes out. Everything that sends mail — registration approval (F20), password reset (F21), campaign mail (F23), inbox replies (F27) — reuses this one interface.

## Design decisions
- **One method, rich message.** `SendAsync(OutboundEmail)` covers system and campaign mail via optional fields (`Headers`, `CustomArgs`, `ReplyTo`, `TrackOpens`, `ListUnsubscribeUrl`). The parent plan's PBI 23.1 names a `SendCampaignEmailAsync`; F23 does **not** add a second interface method — it builds an `OutboundEmail` with those optional fields. (Keeps fakes trivial.)
- **Result, not exceptions, for provider failures**: `SendResult(Success, ProviderMessageId, Error, Retryable)`. Callers (the F23 sender job) decide retry vs fail from `Retryable`; system mail callers just log.
- **Don't rely on setting `Message-ID`.** SendGrid assigns its own RFC `Message-ID` and returns an `X-Message-Id` (its internal id), which is *not* the header recipients' clients quote in `In-Reply-To`. Threading therefore relies on the **Reply-To address encoding** (F23/F25) first; F28's event payload carries the real `smtp-id`/Message-ID if we want header-based matching later. **Verify** against a real send in the F18 verification step and note the finding in the F25 doc.
- **No tracking by default**: click and subscription tracking are off account-wide (F17); per-message `TrackOpens` is opt-in (campaign mail only).

## Files to add/modify

### Application

**`ZenLead.Application/Abstractions/IEmailSender.cs`** (new)
```csharp
namespace ZenLead.Application.Abstractions;

public record EmailAddress(string Address, string? Name = null);

public record OutboundEmail(
    EmailAddress To,
    string Subject,
    string TextBody,
    string? HtmlBody = null,
    EmailAddress? From = null,                                    // null → configured sender (EmailOptions)
    EmailAddress? ReplyTo = null,
    IReadOnlyDictionary<string, string>? Headers = null,          // e.g. In-Reply-To, References, List-Unsubscribe, List-Unsubscribe-Post
    IReadOnlyDictionary<string, string>? CustomArgs = null,       // echoed back by SendGrid webhooks: emailMessageId, workspaceId
    IReadOnlyList<string>? Categories = null,
    bool TrackOpens = false);

/// <param name="Retryable">True for 429/5xx/network errors; false for 4xx (bad address, auth, payload) where retrying cannot help.</param>
public record SendResult(bool Success, string? ProviderMessageId, string? Error = null, bool Retryable = false)
{
    public static SendResult Ok(string? id) => new(true, id);
    public static SendResult Fail(string error, bool retryable) => new(false, null, error, retryable);
}

public interface IEmailSender
{
    Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default);
}
```

**`ZenLead.Application/Email/EmailOptions.cs`** (new) — bound from config section `Email` (defaults added to `appsettings.json` in F17).
```csharp
public class EmailOptions
{
    public string Provider { get; set; } = "Log";                  // "Log" | "SendGrid"
    public string FromAddress { get; set; } = "outreach@leads.zenraxconsultancy.com";
    public string FromName { get; set; } = "Zenrax";
    public bool DomainVerified { get; set; }
    public string InboundDomain { get; set; } = "reply.leads.zenraxconsultancy.com";
}
```
**`ZenLead.Application/Email/SystemEmail.cs`** (new) — the helper F20/F21 use so no feature hand-builds a system message:
```csharp
public static class SystemEmail
{
    /// <summary>Plain text first (always), with a minimal HTML twin. No tracking, no unsubscribe footer: these are account emails.</summary>
    public static OutboundEmail Create(string toAddress, string? toName, string subject, string text, string? actionUrl = null, string? actionLabel = null)
    {
        var html = $"<p>{WebUtility.HtmlEncode(text).Replace("\n", "<br>")}</p>"
                 + (actionUrl is null ? "" : $"<p><a href=\"{WebUtility.HtmlEncode(actionUrl)}\">{WebUtility.HtmlEncode(actionLabel ?? actionUrl)}</a></p>");
        var plain = actionUrl is null ? text : $"{text}\n\n{actionLabel ?? "Open"}: {actionUrl}";
        return new OutboundEmail(new EmailAddress(toAddress, toName), subject, plain, html, Categories: ["system"]);
    }
}
```
(All interpolated user-controlled text is HTML-encoded: display names reach the admin's inbox in F20.)

### Domain / Infrastructure

**`ZenLead.Domain/Entities/Workspace.cs`** (modified)
```csharp
public class Workspace
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";          // IANA id, e.g. "Asia/Dubai"; used by campaign send windows (F22)
    public DateTime CreatedAt { get; set; }
    // AdminEmail arrives in F19/F20
}
```
`WorkspaceConfiguration`: `TimeZone` `HasMaxLength(64).HasDefaultValue("UTC")`. Migration **`AddWorkspaceTimeZone`** (default `'UTC'` backfills existing rows). `WorkspaceRepository.CreateAsync` leaves the default.

**`ZenLead.Domain/Scheduling/TimeZones.cs`** (new) — the one validator both the API and F22 use:
```csharp
public static class TimeZones
{
    public static bool TryFind(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }   // IANA ids work on Windows (ICU) and Linux in .NET 6+
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
```
> **Verify** IANA lookup works on the dev Windows box (ICU is on by default in .NET 8+; if `InvariantGlobalization` is on, IANA ids fail on Windows — check `ZenLead.Api.csproj`/runtimeconfig).

**`ZenLead.Infrastructure/Email/SendGridEmailSender.cs`** (new) — package `SendGrid` in `ZenLead.Infrastructure`.
```csharp
public class SendGridEmailSender(ISendGridClient client, IOptions<EmailOptions> options, ILogger<SendGridEmailSender> logger) : IEmailSender
{
    public async Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        var message = SendGridPayloadMapper.ToMessage(email, options.Value);
        try
        {
            var response = await client.SendEmailAsync(message, ct);
            if (response.IsSuccessStatusCode)
                return SendResult.Ok(response.Headers.TryGetValues("X-Message-Id", out var ids) ? ids.FirstOrDefault() : null);

            var body = await response.Body.ReadAsStringAsync(ct);
            var retryable = (int)response.StatusCode is 429 or >= 500;
            logger.LogWarning("SendGrid rejected a message: {Status} (retryable: {Retryable})", (int)response.StatusCode, retryable); // never log the body/recipient
            return SendResult.Fail($"SendGrid {(int)response.StatusCode}: {Truncate(body, 300)}", retryable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return SendResult.Fail("SendGrid unreachable or timed out.", retryable: true);
        }
    }
}
```
**`SendGridPayloadMapper.cs`** (same folder, `public static`, the unit-tested part)
```csharp
public static SendGridMessage ToMessage(OutboundEmail e, EmailOptions o)
{
    var from = e.From ?? new EmailAddress(o.FromAddress, o.FromName);
    var msg = new SendGridMessage
    {
        From = new SendGrid.Helpers.Mail.EmailAddress(from.Address, from.Name),
        Subject = e.Subject,
        PlainTextContent = e.TextBody,
        HtmlContent = e.HtmlBody
    };
    msg.AddTo(e.To.Address, e.To.Name);
    if (e.ReplyTo is not null) msg.SetReplyTo(new SendGrid.Helpers.Mail.EmailAddress(e.ReplyTo.Address, e.ReplyTo.Name));
    if (e.Headers is not null) foreach (var (k, v) in e.Headers) msg.AddHeader(k, v);
    if (e.CustomArgs is not null) foreach (var (k, v) in e.CustomArgs) msg.AddCustomArg(k, v);
    if (e.Categories is not null) msg.AddCategories(e.Categories.ToList());
    msg.SetClickTracking(false, false);
    msg.SetOpenTracking(e.TrackOpens);
    msg.SetSubscriptionTracking(false);
    return msg;
}
```
Header injection: `AddHeader` values come from our own builders, but `Headers`/`CustomArgs` values must never contain CR/LF — the mapper throws `ArgumentException` if any key/value does (guards F23's lead-derived strings). SendGrid's own limits: custom args total ≤ 10 KB; header names can't be reserved (`From`, `To`…) — the mapper rejects those.

**`ZenLead.Infrastructure/Email/LogEmailSender.cs`** (new) — dev fake: logs `To`, `Subject` and the full text body at `Information` (so approval/reset links are clickable from the console), returns `SendResult.Ok("log-" + Guid)`. Used when `Email:Provider = Log`.

**DI (`Program.cs`)**
```csharp
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
if (string.Equals(builder.Configuration["Email:Provider"], "SendGrid", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ISendGridClient>(_ => new SendGridClient(new SendGridClientOptions { ApiKey = builder.Configuration["SendGrid:ApiKey"] }));
    builder.Services.AddScoped<IEmailSender, SendGridEmailSender>();
}
else builder.Services.AddScoped<IEmailSender, LogEmailSender>();
```
`StartupConfiguration`: new rule `ValidateEmail(configuration, environment)` — provider `SendGrid` requires `SendGrid:ApiKey`; **outside Development, provider must be `SendGrid`** (a prod deploy that silently logs reset links to the console would be a security bug). Update `StartupConfiguration.Validate` to take `IHostEnvironment` (default parameter keeps `StartupAndClaimsTests` compiling; add cases).
`appsettings.Development.json`: `"Email": { "Provider": "Log" }`. User-secrets override to `SendGrid` locally when testing real delivery.

### Api (small additions)

**`ZenLead.Api/Controllers/V1/OpsController.cs`** (F14, modified) — add the demo/diagnostic endpoint:
```csharp
[HttpPost("test-email")]
[EnableRateLimiting(RateLimiting.AuthPolicy)]
public async Task<IActionResult> TestEmail(CancellationToken ct)
{
    if (!IsOps()) return Forbid();
    var to = User.FindFirstValue("email")!;                  // always the caller's own address — never a free-form recipient
    var result = await sender.SendAsync(SystemEmail.Create(to, null, "ZenLead test email",
        "If you can read this, outbound email from ZenLead is working."), ct);
    return result.Success ? Ok(new { sent = true, providerMessageId = result.ProviderMessageId }) : StatusCode(502, new { sent = false, error = result.Error });
}
```
**Workspace time zone** (small addition so the field is usable): `PUT /api/v1/workspaces/current/time-zone { timeZone }` in `WorkspacesController` (tenant-authorised; validates with `TimeZones.TryFind`; any workspace member for now — admin-only would need roles, which are deferred). `IWorkspaceRepository.UpdateTimeZoneAsync(id, tz)`. Angular exposure is part of F24's campaign settings (it shows the zone next to the send window).

## Tests
- **`SendGridPayloadMapperTests`** — from/to/subject/text/html mapping; `From` defaults to configured sender and honours an override; ReplyTo set; headers and custom args present; categories; click/subscription tracking **off** and open tracking follows `TrackOpens`; CR/LF in a header value or reserved header name → `ArgumentException`; HTML omitted when null.
- **`SendGridEmailSenderTests`** — fake `ISendGridClient` (hand-rolled): 202 + `X-Message-Id` → `Ok(id)`; 400/401/403 → `Fail(retryable:false)`; 429 and 503 → `Fail(retryable:true)`; `HttpRequestException` → retryable; caller cancellation propagates (not swallowed); body truncated in the error string.
- **`SystemEmailTests`** — HTML encodes `<script>`/quotes in display text and URL; plain text always contains the link.
- **`LogEmailSenderTests`** — returns success, logs the body (captured `ILogger`).
- **`StartupConfigurationTests`** (extend `StartupAndClaimsTests`) — `Email:Provider=SendGrid` without key → problem; `Log` provider in `Production` → problem; `Log` in `Development` fine.
- **`TimeZonesTests`** — `"Asia/Dubai"`, `"Europe/London"`, `"UTC"` found; `""`, `"Mars/Base"` rejected.
- `FakeEmailSender` in `ZenLead.Tests/Support` (records sent messages, configurable `SendResult`) — used by F20/F21/F23/F27.

## Not in this feature
Campaign sending logic and caps (F23), bounce/complaint handling (F28), HTML templating beyond `SystemEmail`, attachments, per-workspace senders, SendGrid API-based domain-verification checks.

## Verification
1. `dotnet ef migrations add AddWorkspaceTimeZone …`; `dotnet test`.
2. With `Email:Provider=Log`: call `POST /api/v1/ops/test-email` (signed in as an address in `Hangfire:AdminEmails`) → the message appears in the API console.
3. Switch user-secrets to `Email:Provider=SendGrid` + the F17 key: call it again → the email arrives in your inbox (check *Headers*: DKIM `pass` for `leads.zenraxconsultancy.com`, no click-tracking rewritten links). Record the real `Message-ID` vs the returned `X-Message-Id` in `docs/sendgrid-dns-status.md` (feeds the F25 threading decision).
4. A non-ops user gets 403 from `test-email`.
