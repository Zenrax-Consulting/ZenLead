using FluentValidation;
using ZenLead.Application.Dtos.Leads;

namespace ZenLead.Application.Validation.Leads;

public class CreateLeadRequestValidator : AbstractValidator<CreateLeadRequest>
{
    public CreateLeadRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Title).MaximumLength(200);
    }
}
