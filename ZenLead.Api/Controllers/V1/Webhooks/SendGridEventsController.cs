using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Webhooks;
using ZenLead.Application.Webhooks;

namespace ZenLead.Api.Controllers.V1.Webhooks;

/// <summary>
/// SendGrid signed Event Webhook (F28). Anonymous; the ECDSA signature over timestamp + raw body is the credential and is checked
/// before anything is parsed (401 otherwise). Once authentic, any well-formed batch is acknowledged with 200 — duplicates and
/// events for unknown messages included — because SendGrid retries non-2xx for 24 h.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/webhooks/sendgrid/events")]
[RequestSizeLimit(MaxRequestBytes)]
public class SendGridEventsController(IEventSignatureVerifier verifier, ProcessEmailEventsUseCase process, ILogger<SendGridEventsController> logger) : ControllerBase
{
    public const int MaxRequestBytes = 8_388_608;
    public const string SignatureHeader = "X-Twilio-Email-Event-Webhook-Signature";
    public const string TimestampHeader = "X-Twilio-Email-Event-Webhook-Timestamp";

    /// <remarks>No bound parameters: the signature covers the exact bytes, so we read the raw body ourselves.</remarks>
    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        var ct = HttpContext.RequestAborted;
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var body = ms.ToArray();

        if (!verifier.Verify(body, Request.Headers[TimestampHeader].ToString(), Request.Headers[SignatureHeader].ToString()))
        {
            logger.LogWarning("Event webhook rejected: bad or missing signature");
            return Unauthorized();
        }

        List<RawEmailEvent>? events;
        try
        {
            events = JsonSerializer.Deserialize<List<RawEmailEvent>>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            logger.LogWarning("Event webhook: signed body is not a JSON event array");
            return BadRequest();                                                 // signed but not our shape: a real error worth surfacing
        }
        if (events is null) return BadRequest();

        await process.ExecuteAsync(events.Where(e => e is not null).ToList(), ct);
        return Ok();
    }
}
