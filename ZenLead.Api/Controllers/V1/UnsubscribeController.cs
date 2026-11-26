using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZenLead.Application.UseCases.Sending;

namespace ZenLead.Api.Controllers.V1;

public record UnsubscribeInfoResponse(string Email, bool AlreadyUnsubscribed);

/// <summary>
/// Public unsubscribe (F23.4); the signed token is the credential. The email link opens an Angular page that reads with GET and
/// confirms with POST, so link-prefetching mail scanners can't unsubscribe anyone. The <c>List-Unsubscribe</c> header points at the
/// same POST (RFC 8058 one-click): the body is ignored, so the form post Gmail/Yahoo send works. Any bad token → the same 404.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/unsubscribe")]
[EnableRateLimiting(RateLimiting.UnsubscribePolicy)]
public class UnsubscribeController(UnsubscribeUseCase unsubscribe) : ControllerBase
{
    // one fixed body for every failure, so a response never tells a bad token from an unknown lead
    private static readonly object InvalidLink = new { message = "This unsubscribe link isn't valid." };

    /// <summary>Read-only: never changes anything.</summary>
    [HttpGet("{token}")]
    public async Task<ActionResult<UnsubscribeInfoResponse>> Get(string token, CancellationToken ct)
    {
        var result = await unsubscribe.GetAsync(token, ct);
        return result.Valid ? Ok(new UnsubscribeInfoResponse(result.MaskedEmail!, result.AlreadyUnsubscribed)) : NotFound(InvalidLink);
    }

    [HttpPost("{token}")]
    public async Task<IActionResult> Post(string token, CancellationToken ct)
        => (await unsubscribe.ExecuteAsync(token, ct)).Valid ? NoContent() : NotFound(InvalidLink);
}
