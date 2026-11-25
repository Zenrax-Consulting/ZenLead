using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Email;

/// <summary>
/// Development stand-in used when Email:Provider is "Log": writes the message to the log instead of sending it,
/// so approval and reset links are clickable from the console. Startup refuses this provider outside Development.
/// </summary>
public class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        var id = "log-" + Guid.NewGuid();
        logger.LogInformation("Email (not sent, Email:Provider=Log) {MessageId}\nTo: {To}\nSubject: {Subject}\n{Body}",
            id, email.To.Address, email.Subject, email.TextBody);
        return Task.FromResult(SendResult.Ok(id));
    }
}
