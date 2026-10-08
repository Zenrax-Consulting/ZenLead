using FluentValidation;
using ZenLead.Application.Dtos.Discovery;

namespace ZenLead.Application.Validation.Discovery;

public class SuggestFromLeadsRequestValidator : AbstractValidator<SuggestFromLeadsRequest>
{
    public SuggestFromLeadsRequestValidator()
    {
        RuleFor(x => x.LeadIds).Must(ids => ids is { Count: >= 1 and <= 500 }).WithMessage("Select between 1 and 500 leads.");
    }
}
