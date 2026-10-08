using FluentValidation;
using ZenLead.Application.Discovery;
using ZenLead.Application.Dtos.Discovery;

namespace ZenLead.Application.Validation.Discovery;

public class StartRunRequestValidator : AbstractValidator<StartRunRequest>
{
    public StartRunRequestValidator(LeadSourceOptions options)
    {
        RuleFor(x => x.MaxLeads).InclusiveBetween(1, options.MaxLeadsPerRun);
    }
}
