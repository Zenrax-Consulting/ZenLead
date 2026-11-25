using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendGrid;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Email;

namespace ZenLead.Infrastructure.Email;

public class SendGridEmailSender(ISendGridClient client, IOptions<EmailOptions> options, ILogger<SendGridEmailSender> logger) : IEmailSender
{
    public const int MaxErrorBodyLength = 300;

    public async Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        var message = SendGridPayloadMapper.ToMessage(email, options.Value);
        try
        {
            var response = await client.SendEmailAsync(message, ct);
            if (response.IsSuccessStatusCode)
                return SendResult.Ok(response.Headers.TryGetValues("X-Message-Id", out var ids) ? ids.FirstOrDefault() : null);

            var body = response.Body is null ? "" : await response.Body.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            var retryable = status is 429 or >= 500;
            logger.LogWarning("SendGrid rejected a message: {Status} (retryable: {Retryable})", status, retryable); // never log the body/recipient
            return SendResult.Fail($"SendGrid {status}: {Truncate(body, MaxErrorBodyLength)}", retryable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("SendGrid unreachable or timed out ({ExceptionType}).", ex.GetType().Name);
            return SendResult.Fail("SendGrid unreachable or timed out.", retryable: true);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
