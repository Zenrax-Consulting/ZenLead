using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting(RateLimiting.AuthPolicy)]
public class AuthController(
    RegisterWorkspaceUseCase registerWorkspace,
    LoginUseCase login,
    RefreshTokenUseCase refreshToken,
    IValidator<RegisterRequest> registerValidator,
    IValidator<LoginRequest> loginValidator) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var validation = await registerValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationProblem(ToModelState(validation));

        try
        {
            return await registerWorkspace.ExecuteAsync(request, ct);
        }
        catch (EmailAlreadyRegisteredException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (RegistrationFailedException ex)
        {
            var modelState = new ModelStateDictionary();
            foreach (var error in ex.Errors)
                modelState.AddModelError(nameof(RegisterRequest.Password), error);
            return ValidationProblem(modelState);
        }
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var validation = await loginValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationProblem(ToModelState(validation));

        var result = await login.ExecuteAsync(request, ct);
        return result is null ? Unauthorized() : Ok(result);
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return Unauthorized();

        var result = await refreshToken.ExecuteAsync(request, ct);
        return result is null ? Unauthorized() : Ok(result);
    }

    private ModelStateDictionary ToModelState(FluentValidation.Results.ValidationResult validation)
    {
        var modelState = new ModelStateDictionary();
        foreach (var error in validation.Errors)
            modelState.AddModelError(error.PropertyName, error.ErrorMessage);
        return modelState;
    }
}
