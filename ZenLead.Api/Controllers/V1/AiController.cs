using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Sending;
using ZenLead.Application.UseCases.Ai;

namespace ZenLead.Api.Controllers.V1;

public record ComposeEmailApiRequest(Guid LeadId, string? Context);
public record ComposeEmailApiResponse(string Subject, string Body, int TokensUsed);
public record TokenUsageResponse(int Calls, long TotalTokens, decimal EstimatedCostUsd, AiBudgetStatus Budget);

[ApiController]
[Authorize]
[Route("api/v1/ai")]
public class AiController(
    ComposeEmailUseCase composeEmail,
    IValidator<ComposeEmailRequest> validator,
    ITokenUsageTracker usage,
    IAiBudget budget) : ControllerBase
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
        catch (AiBudgetExceededException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { code = "ai_budget_exceeded", message = ex.Message });
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

    /// <summary>Usage for the caller's own workspace only — never a cross-tenant total — plus this month's budget (F23.3).</summary>
    [HttpGet("token-usage")]
    public async Task<ActionResult<TokenUsageResponse>> GetTokenUsage(CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var summary = await usage.GetSummaryAsync(workspaceId, ct);
        return Ok(new TokenUsageResponse(summary.Calls, summary.TotalTokens, summary.EstimatedCostUsd, await budget.GetStatusAsync(workspaceId, ct)));
    }
}
