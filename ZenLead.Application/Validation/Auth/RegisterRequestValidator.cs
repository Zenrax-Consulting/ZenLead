using FluentValidation;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Validation;

namespace ZenLead.Application.Validation.Auth;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.WorkspaceId).NotEmpty().WithMessage("Choose a workspace from the list.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).MustBeStrongPassword();   // shared with reset/change; mirrors the Identity options (PasswordRules)
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
    }
}
