using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Infrastructure.Ai;

namespace ZenLead.Api.Controllers.V1;

public record ComposeEmailApiRequest(Guid LeadId, string? Context);
public record ComposeEmailApiResponse(string Subject, string Body, int TokensUsed);

[ApiController]
[Authorize]
[Route("api/v1/ai")]
public class AiController(ComposeEmailUseCase composeEmail) : ControllerBase
{
    [HttpPost("compose-email")]
    public async Task<ActionResult<ComposeEmailApiResponse>> ComposeEmail(ComposeEmailApiRequest request, CancellationToken ct)
    {
        var workspaceId = Guid.Parse(User.FindFirstValue("workspace_id")!);

        try
        {
            var result = await composeEmail.ExecuteAsync(new ComposeEmailRequest(request.LeadId, request.Context), workspaceId, ct);
            return result is null
                ? NotFound()
                : Ok(new ComposeEmailApiResponse(result.Subject, result.Body, result.TokensUsed));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // the OpenAI HttpClient's own 10s timeout fired, not the caller cancelling the request
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "AI provider timed out. Please try again." });
        }
    }

    [HttpGet("token-usage")]
    public ActionResult<object> GetTokenUsage() => Ok(new { totalTokens = TokenUsageTracker.Total });
}
