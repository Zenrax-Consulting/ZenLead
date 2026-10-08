using FluentValidation;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Discovery;
using ZenLead.Application.Dtos.Discovery;

namespace ZenLead.Application.Validation.Discovery;

public class TargetProfileRequestValidator : AbstractValidator<TargetProfileRequest>
{
    private const int MaxItems = 20;
    private const int MaxItemLength = 100;

    public TargetProfileRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Criteria).NotNull().WithMessage("Criteria are required.");

        When(x => x.Criteria is not null, () =>
        {
            RuleFor(x => x.Criteria.JobTitles).Must(ValidList).WithMessage(ListMessage("Job titles"));
            RuleFor(x => x.Criteria.Industries).Must(ValidList).WithMessage(ListMessage("Industries"));
            RuleFor(x => x.Criteria.Countries).Must(ValidList).WithMessage(ListMessage("Countries"));
            RuleFor(x => x.Criteria.CompanyDomains).Must(ValidList).WithMessage(ListMessage("Company domains"));
            RuleFor(x => x.Criteria.CompanyDomains)
                .Must(d => (d ?? []).All(v => CompanyKey.NormalizeDomain(v) is not null))
                .WithMessage("Company domains must look like example.com.");
            RuleFor(x => x.Criteria.CompanySizeMin).GreaterThanOrEqualTo(0).When(x => x.Criteria.CompanySizeMin is not null);
            RuleFor(x => x.Criteria.CompanySizeMax).GreaterThanOrEqualTo(0).When(x => x.Criteria.CompanySizeMax is not null);
            RuleFor(x => x.Criteria)
                .Must(c => c.CompanySizeMin is null || c.CompanySizeMax is null || c.CompanySizeMin <= c.CompanySizeMax)
                .WithMessage("Company size minimum must not exceed the maximum.");
            // an empty profile would pull random people and burn credits
            RuleFor(x => x.Criteria)
                .Must(c => !LeadCriteriaJson.ToCriteria(c).IsEmpty)
                .WithMessage("Add at least one criterion.");
        });
    }

    private static bool ValidList(IReadOnlyList<string>? list)
        => list is null || (list.Count <= MaxItems && list.All(v => v is not null && v.Length <= MaxItemLength));

    private static string ListMessage(string label) => $"{label}: at most {MaxItems} items of {MaxItemLength} characters each.";
}
