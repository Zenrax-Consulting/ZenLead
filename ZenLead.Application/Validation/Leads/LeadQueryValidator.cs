using FluentValidation;
using ZenLead.Application.Dtos.Leads;

namespace ZenLead.Application.Validation.Leads;

public class LeadQueryValidator : AbstractValidator<LeadQuery>
{
    private static readonly string[] SortFields = ["name", "email", "status", "company", "createdAt"];

    public LeadQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Q).MaximumLength(100);
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Source).IsInEnum();
        RuleFor(x => x.Sort)
            .Must(s => SortFields.Contains(s!.StartsWith('-') ? s[1..] : s))
            .When(x => x.Sort is not null)
            .WithMessage("Sort must be one of: name, email, status, company, createdAt (optionally prefixed with '-').");
    }
}
