using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Ai;

namespace ZenLead.Api.Controllers.V1;

public record ComposeEmailApiRequest(Guid LeadId, string? Context);
public record ComposeEmailApiResponse(string Subject, string Body, int TokensUsed);

[ApiController]
[Authorize]
[Route("api/v1/ai")]
public class AiController(
    ComposeEmailUseCase composeEmail,
    IValidator<ComposeEmailRequest> validator,
    ITokenUsageTracker usage) : ControllerBase
{
    [HttpPost("compose-email")]
    [EnableRateLimiting(RateLimiting.ComposePolicy)]
    public async Task<ActionResult<ComposeEmailApiResponse>> ComposeEmail(ComposeEmailApiRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var composeRequest = new ComposeEmailRequest(request.LeadId, request.Context);

        var validation = await validator.ValidateAsync(composeRequest, ct);
        if (!validation.IsValid)
            return ValidationProblem(validation.ToModelState());

        try
        {
            var result = await composeEmail.ExecuteAsync(composeRequest, workspaceId, ct);
            return result is null
                ? NotFound()
                : Ok(new ComposeEmailApiResponse(result.Subject, result.Body, result.TokensUsed));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // the OpenAI HttpClient's own 10s timeout fired, not the caller cancelling the request
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "AI provider timed out. Please try again." });
        }
        catch (AiProviderException ex)
        {
            return ex.Kind switch
            {
                AiProviderFailureKind.RateLimited => StatusCode(StatusCodes.Status429TooManyRequests,
                    new { message = "The AI provider is rate limiting requests. Please wait a moment and try again." }),
                AiProviderFailureKind.InvalidResponse => StatusCode(StatusCodes.Status502BadGateway,
                    new { message = "The AI provider returned an unusable draft. Please try again." }),
                _ => StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { message = "The AI provider is currently unavailable. Please try again later." })
            };
        }
    }

    /// <summary>Usage for the caller's own workspace only — never a cross-tenant total.</summary>
    [HttpGet("token-usage")]
    public async Task<ActionResult<AiUsageSummary>> GetTokenUsage(CancellationToken ct)
        => this.WorkspaceId() is { } workspaceId
            ? Ok(await usage.GetSummaryAsync(workspaceId, ct))
            : Unauthorized();
}
