using FluentValidation;

namespace ZenLead.Application.Validation;

/// <summary>
/// The one password policy for register, reset and change. Mirrors the Identity password options in Program.cs
/// (length 8 + digit/lower/upper/symbol), so a password these rules accept is never rejected later by Identity.
/// </summary>
public static class PasswordRules
{
    public static IRuleBuilderOptions<T, string> MustBeStrongPassword<T>(this IRuleBuilder<T, string> rule)
        => rule.NotEmpty().MinimumLength(8)
               .Matches("[0-9]").WithMessage("Password must contain a digit.")
               .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
               .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
               .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain a non-alphanumeric character.");
}
