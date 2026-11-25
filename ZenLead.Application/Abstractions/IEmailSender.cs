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

/// <summary>The one outbound-mail port: system mail (approval, password reset) and campaign mail all go through it.</summary>
public interface IEmailSender
{
    Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default);
}
