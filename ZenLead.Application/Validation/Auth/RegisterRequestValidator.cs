using FluentValidation;
using ZenLead.Application.Dtos.Auth;

namespace ZenLead.Application.Validation.Auth;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.WorkspaceName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        // Mirrors the Identity password options in Program.cs (length 8 + digit/lower/upper/symbol),
        // so a password the validator accepts is never rejected later by Identity.
        RuleFor(x => x.Password).NotEmpty()
            .MinimumLength(8)
            .Matches("[0-9]").WithMessage("Password must contain a digit.")
            .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
            .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
            .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain a non-alphanumeric character.");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
    }
}
