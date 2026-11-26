using FluentValidation;
using ZenLead.Application.Dtos.Admin;
using ZenLead.Domain.Leads;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Application.Validation.Admin;

public class CreateWorkspaceRequestValidator : AbstractValidator<CreateWorkspaceRequest>
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 200;

    public CreateWorkspaceRequestValidator()
    {
        RuleFor(x => (x.Name ?? string.Empty).Trim())
            .NotEmpty().WithMessage("Workspace name is required.")
            .Length(NameMinLength, NameMaxLength)
            .OverridePropertyName(nameof(CreateWorkspaceRequest.Name));

        RuleFor(x => x.AdminEmail)
            .Must(e => LeadEmail.Normalize(e) is not null).WithMessage("Enter a valid admin email address.")
            .MaximumLength(LeadEmail.MaxLength);

        RuleFor(x => x.TimeZone)
            .Must(tz => TimeZones.TryFind(tz, out _)).WithMessage("Unknown time zone.")
            .When(x => !string.IsNullOrWhiteSpace(x.TimeZone));
    }
}
