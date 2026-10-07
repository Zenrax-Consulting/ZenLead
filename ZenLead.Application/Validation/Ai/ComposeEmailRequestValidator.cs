using FluentValidation;
using ZenLead.Application.UseCases.Ai;

namespace ZenLead.Application.Validation.Ai;

public class ComposeEmailRequestValidator : AbstractValidator<ComposeEmailRequest>
{
    public const int MaxContextLength = 1000;

    public ComposeEmailRequestValidator()
    {
        RuleFor(x => x.LeadId).NotEmpty();
        RuleFor(x => x.Context).MaximumLength(MaxContextLength); // bounds prompt size, and so cost per call
    }
}
