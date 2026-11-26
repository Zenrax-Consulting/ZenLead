using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;
using ZenLead.Domain.Entities;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting(RateLimiting.AuthPolicy)]
public class AuthController(
    RequestRegistrationUseCase requestRegistration,
    GetApprovalInfoUseCase approvalInfo,
    DecideApprovalUseCase decideApproval,
    LoginUseCase login,
    RefreshTokenUseCase refreshToken,
    IValidator<RegisterRequest> registerValidator,
    IValidator<LoginRequest> loginValidator) : ControllerBase
{
    public const string AcceptedMessage =
        "Request received. If the address can be registered, your workspace administrator has been asked to approve it.";

    /// <summary>Asks to join an existing workspace. Same 202 whether the address is new or already known (no account enumeration).</summary>
    [HttpPost("register")]
    [EnableRateLimiting(RateLimiting.RegisterPolicy)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var validation = await registerValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationProblem(validation.ToModelState());

        try
        {
            return await requestRegistration.ExecuteAsync(request, ct) switch
            {
                RegistrationOutcome.Accepted => Accepted(new RegistrationAcceptedResponse(AcceptedMessage)),
                RegistrationOutcome.UnknownWorkspace => WorkspaceProblem("Choose a workspace from the list."),
                RegistrationOutcome.WorkspaceNotAccepting => Conflict(new { message = "That workspace is not accepting registrations yet." }),
                RegistrationOutcome.TooManyPending => StatusCode(StatusCodes.Status429TooManyRequests,
                    new { message = "Too many pending requests for that workspace. Please try again later." }),
                _ => StatusCode(StatusCodes.Status500InternalServerError)
            };
        }
        catch (RegistrationFailedException ex)
        {
            var modelState = new ModelStateDictionary();
            foreach (var error in ex.Errors)
                modelState.AddModelError(nameof(RegisterRequest.Password), error);
            return ValidationProblem(modelState);
        }
        // EmailAlreadyRegisteredException is handled inside the use case and never reaches here
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var validation = await loginValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationProblem(validation.ToModelState());

        var result = await login.ExecuteAsync(request, ct);
        return result.Outcome switch
        {
            LoginOutcome.Success => Ok(result.Tokens),
            LoginOutcome.PendingApproval => StatusCode(StatusCodes.Status403Forbidden,
                new { code = "pending_approval", message = "Your account is awaiting approval by your workspace administrator." }),
            LoginOutcome.NotApproved => StatusCode(StatusCodes.Status403Forbidden,
                new { code = "not_approved", message = "Your registration was not approved." }),
            _ => Unauthorized()
        };
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return Unauthorized();

        var result = await refreshToken.ExecuteAsync(request, ct);
        return result is null ? Unauthorized() : Ok(result);
    }

    /// <summary>What the approval page shows. Never changes state (mail scanners prefetch links).</summary>
    [HttpGet("approvals/{token}")]
    public async Task<ActionResult<ApprovalInfoResponse>> ApprovalInfo(string token, CancellationToken ct)
        => await approvalInfo.ExecuteAsync(token, ct) is { } info ? Ok(info) : NotFound();

    [HttpPost("approvals/{token}/approve")]
    public Task<IActionResult> Approve(string token, CancellationToken ct) => Decide(token, ApprovalDecision.Approved, ct);

    [HttpPost("approvals/{token}/reject")]
    public Task<IActionResult> Reject(string token, CancellationToken ct) => Decide(token, ApprovalDecision.Rejected, ct);

    private async Task<IActionResult> Decide(string token, ApprovalDecision decision, CancellationToken ct)
        => await decideApproval.ExecuteAsync(token, decision, ct) switch
        {
            DecisionResult.Done => NoContent(),
            DecisionResult.AlreadyUsed => Conflict(new { code = "already_used", message = "This link has already been used." }),
            DecisionResult.Expired => StatusCode(StatusCodes.Status410Gone, new { code = "expired", message = "This link has expired." }),
            _ => NotFound()
        };

    private ActionResult WorkspaceProblem(string message)
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError(nameof(RegisterRequest.WorkspaceId), message);
        return ValidationProblem(modelState);
    }
}
