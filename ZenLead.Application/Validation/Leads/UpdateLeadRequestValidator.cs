using FluentValidation;
using ZenLead.Application.Dtos.Leads;

namespace ZenLead.Application.Validation.Leads;

public class UpdateLeadRequestValidator : AbstractValidator<UpdateLeadRequest>
{
    public UpdateLeadRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.CompanyName).MaximumLength(200);
        RuleFor(x => x.CompanyDomain).MaximumLength(253);
    }
}
