using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Inbound;
using ZenLead.Domain.Entities;

namespace ZenLead.Api.Controllers.V1.Webhooks;

/// <summary>
/// SendGrid Inbound Parse (F25). Anonymous; the shared secret in the query string is the credential. Once the request is
/// authentic it is always acknowledged with 200 — attached, duplicate and quarantined mail alike — because SendGrid retries
/// non-2xx for days. Only a bad secret gets 401 (and nothing is parsed before the check); a non-form post gets 400.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/webhooks/sendgrid/inbound")]
[RequestSizeLimit(MaxRequestBytes)]                                    // SendGrid caps messages at 30 MB; we ignore attachments but must accept the post
[RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes, ValueLengthLimit = MaxRequestBytes)]   // raw-MIME mode puts the whole message in one field
public class SendGridInboundController(
    IInboundEmailParser parser,
    ProcessInboundEmailUseCase process,
    IOptions<WebhookOptions> options,
    IWebHostEnvironment environment,
    TimeProvider clock,
    ILogger<SendGridInboundController> logger) : ControllerBase
{
    /// <summary>32 MiB: above SendGrid's 30 MB message cap, so no post SendGrid can make is refused; anything larger is quarantined <c>too_large</c>.</summary>
    public const int MaxRequestBytes = 33_554_432;

    /// <remarks>
    /// Deliberately no bound parameters: MVC's form value provider would read (and size-check) the whole body during model
    /// binding, i.e. before the secret is checked, and turn an oversized post into an automatic 400.
    /// </remarks>
    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        var ct = HttpContext.RequestAborted;
        var secret = Request.Query["secret"].Count == 1 ? Request.Query["secret"][0] : null;
        if (!SecretMatches(secret, options.Value.InboundSecret)) return Unauthorized();       // no body parsing before auth
        if (!Request.HasFormContentType) return BadRequest();

        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            // over the size limits: keep a trace and acknowledge, or SendGrid would retry the same oversized post for days
            await process.QuarantineRawAsync(InboundQuarantine.TooLarge, new Dictionary<string, string>(), ct);
            logger.LogWarning("Inbound post rejected by the form limits: {Reason}", ex.Message);
            return Ok();
        }

        var fields = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);   // files (attachments) are never read
        if (environment.IsDevelopment() && options.Value.CaptureRaw) await CaptureAsync(fields, ct);

        try
        {
            await process.ExecuteAsync(parser.Parse(fields, clock.GetUtcNow().UtcDateTime), ct);
            return Ok();                                                                       // attached / duplicate / quarantined: all acknowledged
        }
        catch (InboundParseException ex)
        {
            await process.QuarantineRawAsync(InboundQuarantine.ParseError, fields, ct);       // keep the evidence, then ack so SendGrid stops retrying
            logger.LogWarning("Unparseable inbound post: {Reason}", ex.Message);
            return Ok();
        }
    }

    private static bool SecretMatches(string? provided, string? expected)
        => !string.IsNullOrEmpty(expected) && provided is not null
           && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    /// <summary>Fixture collection (Development only). The files hold personal data: strip it before committing any to the repo.</summary>
    private async Task CaptureAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "zenlead-inbound");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{clock.GetUtcNow():yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            await System.IO.File.WriteAllTextAsync(path, JsonSerializer.Serialize(fields, new JsonSerializerOptions { WriteIndented = true }), ct);
            logger.LogInformation("Captured inbound post to {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not capture the inbound post");
        }
    }
}
