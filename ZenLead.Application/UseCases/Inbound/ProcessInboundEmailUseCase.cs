using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Email;
using ZenLead.Application.Inbound;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Inbound;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Application.UseCases.Inbound;

public enum InboundOutcome { Attached, Duplicate, Quarantined }

public record InboundResult(InboundOutcome Outcome, Guid? ThreadId = null, Guid? MessageId = null, string? Reason = null, string? Via = null)
{
    public static readonly InboundResult Duplicate = new(InboundOutcome.Duplicate);
    public static InboundResult Attached(Guid threadId, Guid messageId, string via) => new(InboundOutcome.Attached, threadId, messageId, Via: via);
    public static InboundResult Quarantined(string reason) => new(InboundOutcome.Quarantined, Reason: reason);
}

/// <summary>
/// Attaches an inbound reply (SendGrid Inbound Parse, F25) to its lead's thread and stops the sequence. Never silently drops mail:
/// anything that can't be attached safely is quarantined (headers only, no body). Idempotent on Message-ID, because SendGrid
/// redelivers. Matching, strongest first: (1) the Reply-To token, (2) In-Reply-To/References against our sent messages,
/// (3) the sender's email against leads recently emailed — only when exactly one workspace qualifies.
/// </summary>
public class ProcessInboundEmailUseCase(
    IInboundStore store,
    IHtmlSanitizer sanitizer,
    IOptions<EmailOptions> email,
    IOptions<InboundOptions> inbound,
    TimeProvider clock,
    ILogger<ProcessInboundEmailUseCase> logger)
{
    public const int MaxSubjectLength = 500;
    public const int MaxAddressLength = 256;
    public const int MaxMessageIdLength = 500;
    public const int MaxHeadersSnippet = 4096;

    public const string ViaToken = "reply-to-token";
    public const string ViaHeaders = "headers";
    public const string ViaSenderEmail = "sender-email";

    public async Task<InboundResult> ExecuteAsync(InboundEmail mail, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var auto = AutoReplyDetector.Detect(mail.Headers, mail.FromAddress, mail.Subject);

        // 1) Resolve the target enrollment — strongest signal first
        EnrollmentRef? target = null;
        var via = "";
        foreach (var to in mail.ToAddresses)
            if (ReplyAddress.TryDecode(to, email.Value.InboundDomain, out var enrollmentId) && await store.FindEnrollmentByIdAsync(enrollmentId, ct) is { } byToken)
            {
                target = byToken;
                via = ViaToken;
                break;
            }

        if (target is null && IdsFrom(mail) is { Count: > 0 } ids && await store.FindByMessageReferencesAsync(ids, ct) is { } byHeader)
        {
            target = byHeader;
            via = ViaHeaders;
        }

        if (target is null)
        {
            var candidates = await store.FindRecentByLeadEmailAsync(mail.FromAddress.Trim().ToLowerInvariant(), now.AddDays(-inbound.Value.SenderMatchDays), ct);
            if (candidates.Count == 1)
            {
                target = candidates[0];
                via = ViaSenderEmail;
            }
            else return await QuarantineAsync(mail, candidates.Count == 0 ? InboundQuarantine.NoMatch : InboundQuarantine.Ambiguous, ct);   // never guess between workspaces
        }

        // 2) Sender sanity: someone who learned an enrollment id must not be able to inject replies as the lead
        var mismatch = !string.Equals(mail.FromAddress.Trim(), target.LeadEmail, StringComparison.OrdinalIgnoreCase);
        if (mismatch && !(DkimPasses(mail.Dkim, mail.FromAddress) || SpfPasses(mail.Spf)) && via != ViaSenderEmail)
            return await QuarantineAsync(mail, InboundQuarantine.SpoofSuspected, ct);

        // 3) Persist (idempotent on Message-ID)
        var text = mail.Text ?? HtmlToText(mail.Html);
        var thread = await store.GetOrCreateThreadAsync(target, Truncate(mail.Subject, MaxSubjectLength), now, ct);
        var message = new InboxMessage
        {
            Id = Guid.NewGuid(), WorkspaceId = target.WorkspaceId, ThreadId = thread.Id, Direction = MessageDirection.Inbound,
            FromAddress = Truncate(mail.FromAddress.Trim(), MaxAddressLength), FromName = mail.FromName is null ? null : Truncate(mail.FromName, MaxAddressLength),
            ToAddress = Truncate(mail.ToAddresses.FirstOrDefault() ?? "", MaxAddressLength), Subject = Truncate(mail.Subject, MaxSubjectLength),
            TextBody = ReplyTextCleaner.StripQuoted(text), FullTextBody = text,
            HtmlBody = string.IsNullOrWhiteSpace(mail.Html) ? null : sanitizer.Sanitize(mail.Html),
            OccurredAt = mail.ReceivedAt,
            RawMessageId = Truncate(string.IsNullOrWhiteSpace(mail.MessageId) ? SyntheticId(mail) : mail.MessageId.Trim(), MaxMessageIdLength),
            SenderMismatch = mismatch,
            IsAutoReply = auto != AutoReplyKind.None,
            Classification = auto switch
            {
                AutoReplyKind.OutOfOffice => ReplyClassification.OutOfOffice,
                AutoReplyKind.Bounce => ReplyClassification.Bounce,
                _ => ReplyClassification.Unclassified
            },
            AuthSummary = Truncate($"dkim={Verdict(mail.Dkim)}; spf={Verdict(mail.Spf)}", 100)
        };
        if (!await store.TryAddInboundAsync(message, ct))
        {
            logger.LogInformation("Duplicate inbound delivery for enrollment {EnrollmentId}; acknowledged without effects", target.EnrollmentId);
            return InboundResult.Duplicate;                                                     // SendGrid redelivery: acknowledged, no second effects
        }

        // 4) Effects: only real human replies stop the sequence (auto-replies leave it running)
        var effects = await store.ApplyReplyEffectsAsync(target, isAutoReply: message.IsAutoReply, ct);
        logger.LogInformation("Inbound reply attached via {Via} to enrollment {EnrollmentId} (thread {ThreadId}, message {MessageId}, auto {AutoReply}, stopped {Stopped})",
            via, target.EnrollmentId, thread.Id, message.Id, auto, effects.EnrollmentStopped);       // ids only: no addresses/bodies in logs

        // F26 hooks in here: enqueue classification for non-auto replies (message.Id, target.WorkspaceId)
        return InboundResult.Attached(thread.Id, message.Id, via);
    }

    /// <summary>For posts the parser rejected: keep the evidence (headers only) so nothing is silently dropped.</summary>
    public async Task QuarantineRawAsync(string reason, IReadOnlyDictionary<string, string> fields, CancellationToken ct)
    {
        string? F(string key) => fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        var headers = F("headers") ?? HeaderBlockOf(F("email"));
        await store.AddQuarantineAsync(new InboundQuarantine
        {
            Id = Guid.NewGuid(), ReceivedAt = clock.GetUtcNow().UtcDateTime, Reason = reason,
            FromAddress = TruncateOrNull(F("from"), MaxAddressLength), ToAddress = TruncateOrNull(F("to"), 1000),
            Subject = TruncateOrNull(F("subject"), MaxSubjectLength), HeadersSnippet = TruncateOrNull(headers, MaxHeadersSnippet)
        }, ct);
        logger.LogWarning("Inbound email quarantined: {Reason}", reason);
    }

    private async Task<InboundResult> QuarantineAsync(InboundEmail mail, string reason, CancellationToken ct)
    {
        await store.AddQuarantineAsync(new InboundQuarantine
        {
            Id = Guid.NewGuid(), ReceivedAt = mail.ReceivedAt, Reason = reason,
            FromAddress = Truncate(mail.FromAddress, MaxAddressLength), ToAddress = Truncate(string.Join(", ", mail.ToAddresses), 1000),
            Subject = Truncate(mail.Subject, MaxSubjectLength),
            HeadersSnippet = Truncate(string.Join("\n", mail.Headers.Select(h => $"{h.Key}: {h.Value}")), MaxHeadersSnippet)   // no body: reply content is personal data
        }, ct);
        logger.LogWarning("Inbound email quarantined: {Reason}", reason);
        return InboundResult.Quarantined(reason);
    }

    /// <summary>
    /// <c>In-Reply-To</c> + <c>References</c> ids, plus for each its local part and the segment before the first '.', which is how
    /// SendGrid's <c>X-Message-Id</c> (our <c>ProviderMessageId</c>) shows up inside the Message-ID it generated.
    /// </summary>
    public static IReadOnlyCollection<string> IdsFrom(InboundEmail mail)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (mail.InReplyTo is null ? mail.References : mail.References.Prepend(mail.InReplyTo)))
        {
            var id = raw.Trim().Trim('<', '>').Trim();
            if (id.Length is 0 or > MaxMessageIdLength) continue;
            ids.Add(id);
            var at = id.IndexOf('@');
            var local = at > 0 ? id[..at] : id;
            ids.Add(local);
            var dot = local.IndexOf('.');
            if (dot > 0) ids.Add(local[..dot]);
        }
        return ids;
    }

    /// <summary>
    /// SendGrid's <c>dkim</c> field lists one verdict per signing domain, e.g. <c>{@gmail.com : pass, @sendgrid.net : fail}</c>.
    /// Only a pass for the From domain (or a parent of it) counts: anyone can DKIM-sign with their own domain.
    /// </summary>
    public static bool DkimPasses(string? dkim, string fromAddress)
    {
        if (string.IsNullOrWhiteSpace(dkim)) return false;
        var fromDomain = fromAddress[(fromAddress.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant();
        foreach (var entry in dkim.Trim().Trim('{', '}').Split(','))
        {
            var parts = entry.Split(':', 2);
            if (parts.Length == 1)
            {
                if (parts[0].Trim().Equals("pass", StringComparison.OrdinalIgnoreCase)) return true;     // no domain given
                continue;
            }
            if (!parts[1].Trim().Equals("pass", StringComparison.OrdinalIgnoreCase)) continue;
            var domain = parts[0].Trim().TrimStart('@').ToLowerInvariant();
            if (domain.Length > 0 && (fromDomain == domain || fromDomain.EndsWith("." + domain, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    public static bool SpfPasses(string? spf) => spf is not null && spf.Trim().Equals("pass", StringComparison.OrdinalIgnoreCase);

    private static string Verdict(string? v) => string.IsNullOrWhiteSpace(v) ? "none" : v.Trim().Trim('{', '}').Trim();

    /// <summary>Mail without a Message-ID still dedupes: hash of the fields a redelivery repeats (not our receive time).</summary>
    public static string SyntheticId(InboundEmail mail)
    {
        mail.Headers.TryGetValue("Date", out var date);
        var input = $"{mail.FromAddress}|{date}|{mail.Subject}|{mail.Text ?? mail.Html}";
        return "synthetic:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static readonly Regex ScriptsAndStyles = new(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex LineBreakTags = new(@"<\s*(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex BlankLines = new(@"\n[ \t]*\n(?:[ \t]*\n)+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Text fallback for HTML-only mail. Best effort; the HTML itself is stored sanitised.</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        try
        {
            var s = ScriptsAndStyles.Replace(html, "");
            s = LineBreakTags.Replace(s, "\n");
            s = Tags.Replace(s, "");
            s = WebUtility.HtmlDecode(s).Replace("\r\n", "\n").Replace(' ', ' ');
            return BlankLines.Replace(s, "\n\n").Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    private static string? HeaderBlockOf(string? rawMime)
    {
        if (rawMime is null) return null;
        var end = rawMime.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0) end = rawMime.IndexOf("\n\n", StringComparison.Ordinal);
        return end < 0 ? null : rawMime[..end];                                                // no header/body boundary: keep nothing rather than risk the body
    }

    private static string Truncate(string v, int max) => v.Length <= max ? v : v[..max];
    private static string? TruncateOrNull(string? v, int max) => v is null ? null : Truncate(v, max);
}
